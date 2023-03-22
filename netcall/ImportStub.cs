﻿using Microsoft.Win32.SafeHandles;
using netcall.Win32;
using netcall.Win32.Structs;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using static System.Collections.Specialized.BitVector32;

namespace netcall
{
    internal class ImportStub
    {
        [SuppressUnmanagedCodeSecurity]
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ASMCallgate();

        public List<NtApi> ImportedAPIs { get; set; } = new List<NtApi>();
        public long MappedNtdllSize = 0;

        public bool Import(string[] ntApiNames)
        {
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string ntdll = Path.Combine(directory, "system32", "ntdll.dll");

            if (Environment.Is64BitOperatingSystem)
            {
                if (!Environment.Is64BitProcess) // syswow64
                {
                    ntdll = Path.Combine(directory, "syswow64", "ntdll.dll");
                }
            }

            // Map copy of ntdll
            var mappedNtdll = MapInternal(ntdll);

            if (mappedNtdll == IntPtr.Zero)
            {
                Console.WriteLine("[!!!] Fatal: failed importing stubs.");
                return false;
            }

            using PEUtils pe = new PEUtils(mappedNtdll);

            foreach (string apiName in ntApiNames)
            {
                var api = pe.ResolveAPIExportAddress(apiName);

                if (api != null)
                    this.ImportedAPIs.Add(api);
            }

            // Create space 
            int requiredSize = this.ImportedAPIs.Sum(api => api.Size);

            IntPtr stubspace = Win32API.VirtualAlloc(
                IntPtr.Zero,
                (uint)requiredSize,
                AllocationType.Commit | AllocationType.Reserve,
                MemoryProtection.ExecuteReadWrite
            );

            if (stubspace == IntPtr.Zero)
            {
                Console.WriteLine("[!!!] Fatal: failed allocating space of {0} bytes for {1} apis.", requiredSize, this.ImportedAPIs.Count);
                return false;
            }

            foreach (var stub in ImportedAPIs)
                CopyStub(stubspace, stub);

            return true;
        }

        private IntPtr MapInternal(string modulePath)
        {
            SafeFileHandle sf = File.OpenHandle(
                modulePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.None
            );

            if (sf.IsInvalid)
            {
                Console.WriteLine("[!!!] Failed to create handle: {0}", modulePath);
                return IntPtr.Zero;
            }

            MappedNtdllSize = new FileInfo(modulePath).Length;

            nint hMap = Win32API.CreateFileMapping(
                sf.DangerousGetHandle(),
                nint.Zero,
                MemoryProtection.ReadOnly,
                0,
                0,
                null
            );

            if (hMap == nint.Zero)
            {
                sf.Close();

                Console.WriteLine("[!!!] Failed to create file mapping for ntdll.");

                return IntPtr.Zero;
            }

            nint address = Win32API.MapViewOfFile(
                hMap,
                FileMapAccess.FileMapRead,
                0,
                0,
                0
            );

            Win32API.CloseHandle(hMap);
            sf.Close();

            if (address == nint.Zero)
            {
                Console.WriteLine("[!!!] Failed to map ntdll");

                return IntPtr.Zero;
            }

            Console.WriteLine("Mapped ntdll at 0x{0:x2}", address);

            return address;
        }

        private IntPtr CopyStub(IntPtr space, NtApi api)
        {
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            {
                // wow64
                // fix edx

                CopyMemory(space, api.Address, api.Size, true);
            }

            return IntPtr.Zero;
        }

        // XDDDDDDDDDDDDDDDDDDDDDDDDD
        private unsafe int GetX86SwitchTo64Bitmode()
        {
            int ret;

            byte[] payload =
            {
                0x64, 0xA1, 0xC0, 0x00, 0x00, 0x00, // mov eax, dword ptr fs:[0xC0]
                0xC3                                // ret
            };

            fixed (void* p = payload)
            {
                bool x = Win32API.VirtualProtectEx(
                    -1,
                    (nint)p,
                    (uint)payload.Length,
                    MemoryProtection.ExecuteReadWrite,
                    out var oldProtect
                );

                // didnt know about that
                // credits for this idea: https://stackoverflow.com/questions/18836120/using-c-inline-assembly-in-c-sharp

                var func = Marshal.GetDelegateForFunctionPointer<ASMCallgate>((IntPtr)p);

                ret = func();

                Win32API.VirtualProtectEx(
                    Process.GetCurrentProcess().Handle,
                    (nint)p,
                    (uint)payload.Length,
                    oldProtect,
                    out _
                );
            }

            return ret;
        }

        int offset = 0;
        private void CopyMemory(IntPtr dest, IntPtr source, int size, bool fixWow64 = false)
        {
            byte[] bytes = new byte[size];

            Marshal.Copy(source, bytes, 0, size);

            if (fixWow64)
            {
                for (var i = 0; i < size; i++)
                {
                    if (bytes[i] == 0xBA) // MOV EDX, IMM32
                    {
                        var fix = GetX86SwitchTo64Bitmode();

                        byte[] newptr = BitConverter.GetBytes(fix);

                        bytes[i + 1] = newptr[0];
                        bytes[i + 2] = newptr[1];
                        bytes[i + 3] = newptr[2];
                        bytes[i + 4] = newptr[3];

                        break;
                    }
                }
            }

            for (var i = 0; i < size; i++)
            {
                Marshal.WriteByte(dest, i + offset, bytes[i]);
            }

            offset += size;
        }
    }
}
