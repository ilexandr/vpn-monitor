using System.IO;
using System.Runtime.InteropServices;
using System.Text;
internal static class ProfileProtector
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static void Write(string config,string path)
    {
        var bytes=Encoding.UTF8.GetBytes(config);
        var input=new Blob{Size=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};
        Blob output=default;
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            if(!CryptProtectData(ref input,"VPN Monitor AWG",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,5,out output)) throw new InvalidOperationException();
            var encrypted=new byte[output.Size]; Marshal.Copy(output.Data,encrypted,0,encrypted.Length);
            File.WriteAllBytes(path,encrypted);
        }
        finally
        {
            Array.Clear(bytes);
            for(var i=0;i<input.Size;i++) Marshal.WriteByte(input.Data,i,0);
            Marshal.FreeHGlobal(input.Data);
            if(output.Data!=IntPtr.Zero) LocalFree(output.Data);
        }
    }
}