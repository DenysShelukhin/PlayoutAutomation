// EWTN managed Incoming. GPL-2.0. No manifest or external ingest service.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using TAS.Common;
using TAS.Common.Interfaces.Media;
using TAS.Common.Interfaces.MediaDirectory;
using TAS.Server.Media;
using TAS.Server.MediaOperation;

namespace TAS.Server
{
    internal static class IncomingMediaGate { internal static readonly object Sync = new object(); }

    internal static class IncomingFiles
    {
        [StructLayout(LayoutKind.Sequential)] private struct Info
        {
            public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern SafeFileHandle CreateFileW(string p,uint a,uint s,IntPtr sec,uint d,uint f,IntPtr t);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle h,out Info i);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetFileInformationByHandle(SafeFileHandle h,int c,IntPtr b,uint size);

        internal static string Within(string root,string path)
        {
            root=Path.GetFullPath(root).TrimEnd('\\')+"\\";path=Path.GetFullPath(path);
            if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(path)+"\\",root,StringComparison.OrdinalIgnoreCase))
                throw new IOException("Managed file must be directly inside its configured root.");
            if(Path.GetFileName(path).Contains(":"))throw new IOException("ADS input forbidden.");
            var parent=new DirectoryInfo(root);while(parent!=null){if((parent.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse path forbidden.");parent=parent.Parent;}
            return path;
        }
        internal static FileStream Open(string root,string path)
        {
            path=Within(root,path);var h=CreateFileW(path,0x80010000,0,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            if(h.IsInvalid){int code=Marshal.GetLastWin32Error();h.Dispose();throw new Win32Exception(code);}
            Info i;if(!GetFileInformationByHandle(h,out i) || i.Links!=1 || (i.Attributes&0x410)!=0){h.Dispose();throw new IOException("Only regular single-link managed files allowed.");}
            return new FileStream(h,FileAccess.Read);
        }
        internal static FileStream Inspect(string root,string path)
        {
            path=Within(root,path);var h=CreateFileW(path,0x80000000,7,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            if(h.IsInvalid){int code=Marshal.GetLastWin32Error();h.Dispose();throw new Win32Exception(code);}
            Info i;if(!GetFileInformationByHandle(h,out i) || i.Links!=1 || (i.Attributes&0x410)!=0){h.Dispose();throw new IOException("Unsafe managed object.");}
            return new FileStream(h,FileAccess.Read);
        }
        internal static string Identity(FileStream f)
        {
            Info i;if(!GetFileInformationByHandle(f.SafeFileHandle,out i))throw new Win32Exception();
            return i.Volume+":"+i.IndexHigh+":"+i.IndexLow;
        }
        internal static string Hash(FileStream f)
        {
            f.Position=0;using(var s=SHA256.Create())return BitConverter.ToString(s.ComputeHash(f)).Replace("-","");
        }
        internal static void Seal(string root,string path)
        {
            Within(root,path);
            var security=new System.Security.AccessControl.FileSecurity();security.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{System.Security.Principal.WindowsIdentity.GetCurrent().User,
                new System.Security.Principal.SecurityIdentifier("S-1-5-18"),new System.Security.Principal.SecurityIdentifier("S-1-5-32-544")})
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,System.Security.AccessControl.FileSystemRights.FullControl,System.Security.AccessControl.AccessControlType.Allow));
            File.SetAccessControl(path,security);
        }
        internal static void Move(FileStream f,string destination)
        {
            byte[] name=System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(destination));int root=IntPtr.Size==8?8:4,len=root+IntPtr.Size,start=len+4;
            IntPtr p=Marshal.AllocHGlobal(start+name.Length+2);
            try{for(int k=0;k<start+name.Length+2;k++)Marshal.WriteByte(p,k,0);Marshal.WriteIntPtr(p,root,IntPtr.Zero);Marshal.WriteInt32(p,len,name.Length);Marshal.Copy(name,0,IntPtr.Add(p,start),name.Length);
                if(!SetFileInformationByHandle(f.SafeFileHandle,3,p,(uint)(start+name.Length+2)))throw new Win32Exception(Marshal.GetLastWin32Error());}
            finally{Marshal.FreeHGlobal(p);}
        }
        internal static void Delete(string root,string path,string expectedIdentity,string expectedHash)
        {
            if(!Directory.Exists(root))throw new IOException("Cleanup volume/root unavailable.");
            try{
                using(var f=Open(root,path)){
                    if(Identity(f)!=expectedIdentity || (expectedHash!=null && Hash(f)!=expectedHash))throw new IOException("Cleanup identity/content changed; file retained.");
                    var p=Marshal.AllocHGlobal(4);try{Marshal.WriteInt32(p,1);if(!SetFileInformationByHandle(f.SafeFileHandle,4,p,4))throw new Win32Exception(Marshal.GetLastWin32Error());}finally{Marshal.FreeHGlobal(p);}
                }
            }catch(Win32Exception e) when(e.NativeErrorCode==2){ /* only confirmed missing file, not offline/access denied */ }
        }
    }

    // Native Ingest target outside the watched ServerDirectory. It has no SQL side effects.
    internal sealed class IncomingWorkDirectory : MediaDirectoryBase
    {
        internal readonly System.Collections.Generic.List<IMedia> Readback = new System.Collections.Generic.List<IMedia>();
        public override void AddMedia(IMedia m) { base.AddMedia(m);Readback.Add(m); }
        internal override IMedia CreateMedia(IMediaProperties source)
        {
            var m=new IncomingWorkMedia();m.CloneMediaProperties(source);m.FileName=source.FileName;m.Directory=this;m.MediaStatus=TMediaStatus.Required;return m;
        }
        public override IMediaSearchProvider Search(TMediaCategory? c,string s)=>throw new NotSupportedException();
    }
    internal sealed class IncomingWorkMedia : MediaBase { }

    internal static class IncomingPersistence
    {
        internal static void Save(ServerMedia media,ServerDirectory directory)
        {
            if(media.IdPersistentMedia==0){
                DatabaseProvider.Database.InsertMedia(media,directory.Server.Id);
                // The legacy provider assigns last_insert_rowid even on a swallowed INSERT error.
                // Never retain that unrelated row ID without confirming the new GUID below.
            }else{
                var before=new IncomingWorkDirectory {Folder=directory.Folder};
                DatabaseProvider.Database.LoadServerDirectory<ServerMedia>(before,directory.Server.Id);
                if(!System.Linq.Enumerable.Any(System.Linq.Enumerable.OfType<ServerMedia>(before.Readback),m=>m.MediaGuid==media.MediaGuid && m.IdPersistentMedia==media.IdPersistentMedia)){
                    media.IdPersistentMedia=0;throw new IOException("GUID/row identity not confirmed; refusing UPDATE.");
                }
                DatabaseProvider.Database.UpdateMedia(media,directory.Server.Id);
            }
            // Existing provider API only offers directory readback. One metadata query
            // per commit, into an isolated non-watching sink; never touches live files.
            var sink=new IncomingWorkDirectory {Folder=directory.Folder};
            DatabaseProvider.Database.LoadServerDirectory<ServerMedia>(sink,directory.Server.Id);
            var stored=System.Linq.Enumerable.SingleOrDefault(System.Linq.Enumerable.OfType<ServerMedia>(sink.Readback),m=>m.MediaGuid==media.MediaGuid);
            if(stored==null)media.IdPersistentMedia=0;
            if(stored==null || stored.FileName!=media.FileName || stored.Folder!=media.Folder || stored.FileSize!=media.FileSize || stored.MediaStatus!=media.MediaStatus || stored.DurationPlay!=media.DurationPlay || stored.TcPlay!=media.TcPlay)
                throw new IOException("Media Library commit/readback not confirmed; input retained.");
        }
    }

    internal sealed class IncomingDeleteOperation : DeleteOperation
    {
        internal string Root,PathName,FileIdentity,ContentHash;
        protected override System.Threading.Tasks.Task<bool> InternalExecute()
        {
            return System.Threading.Tasks.Task.Run(()=>{IncomingFiles.Delete(Root,PathName,FileIdentity,ContentHash);return true;});
        }
        public override string ToString()=>"Managed cleanup "+System.IO.Path.GetFileName(PathName);
    }
}
