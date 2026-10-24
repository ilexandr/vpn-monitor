using System.Runtime.InteropServices;
using System.Text;
namespace AwgPrototype;
internal static class MachineProfile
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 512 * 1024) throw new ArgumentException();
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
                throw new InvalidOperationException();
            var clear = new byte[output.Length];
            Marshal.Copy(output.Data, clear, 0, clear.Length);
            try { return Encoding.UTF8.GetString(clear); }
            finally { Array.Clear(clear); for(var i=0;i<output.Length;i++) Marshal.WriteByte(output.Data,i,0); }
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(input.Data);
            if(output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}