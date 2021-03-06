// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using Internal.NativeFormat;
using System.Diagnostics.Contracts;
using System.Runtime.InteropServices;
using System.Text;
using System;
using System.Runtime;

using Debug = System.Diagnostics.Debug;

namespace Internal.Runtime.CompilerHelpers
{
    [McgIntrinsics]
    internal static class StartupCodeHelpers
    {
        public static IntPtr[] Modules
        {
            get; private set;
        }

        [RuntimeExport("InitializeModules")] // TODO: Change to NativeCallable
        internal static void InitializeModules(IntPtr moduleHeaders, int count)
        {
            IntPtr[] modules = CreateModuleManagers(moduleHeaders, count);

            foreach (var moduleManager in modules)
            {
                InitializeGlobalTablesForModule(moduleManager);
            }

            // We are now at a stage where we can use GC statics - publish the list of modules
            // so that the eager constructors can access it.
            Modules = modules;

            // These two loops look funny but it's important to initialize the global tables before running
            // the first class constructor to prevent them calling into another uninitialized module
            foreach (var moduleManager in modules)
            {
                InitializeEagerClassConstructorsForModule(moduleManager);
            }
        }

        internal static unsafe void InitializeCommandLineArgsW(int argc, char** argv)
        {
            string[] args = new string[argc];
            for (int i = 0; i < argc; ++i)
            {
                args[i] = new string(argv[i]);
            }
            Environment.SetCommandLineArgs(args);
        }

        internal static unsafe void InitializeCommandLineArgs(int argc, byte** argv)
        {
            string[] args = new string[argc];
            for (int i = 0; i < argc; ++i)
            {
                byte* argval = argv[i];
                int len = CStrLen(argval);
                args[i] = Encoding.UTF8.GetString(argval, len);
            }
            Environment.SetCommandLineArgs(args);
        }

        private static unsafe IntPtr[] CreateModuleManagers(IntPtr moduleHeaders, int count)
        {
            // Count the number of modules so we can allocate an array to hold the ModuleManager objects.
            // At this stage of startup, complex collection classes will not work.
            int moduleCount = 0;
            for (int i = 0; i < count; i++)
            {
                // The null pointers are sentinel values and padding inserted as side-effect of
                // the section merging. (The global static constructors section used by C++ has 
                // them too.)
                if (((IntPtr *)moduleHeaders)[i] != IntPtr.Zero)
                    moduleCount++;
            }

            IntPtr[] modules = new IntPtr[moduleCount];
            int moduleIndex = 0;
            for (int i = 0; i < count; i++)
            {
                if (((IntPtr *)moduleHeaders)[i] != IntPtr.Zero)
                    modules[moduleIndex++] = CreateModuleManager(((IntPtr *)moduleHeaders)[i]);
            }

            return modules;
        }

        /// <summary>
        /// Each managed module linked into the final binary may have its own global tables for strings,
        /// statics, etc that need initializing. InitializeGlobalTables walks through the modules
        /// and offers each a chance to initialize its global tables.
        /// </summary>
        private static unsafe void InitializeGlobalTablesForModule(IntPtr moduleManager)
        {
            // Configure the module indirection cell with the newly created ModuleManager. This allows EETypes to find
            // their interface dispatch map tables.
            int length;
            IntPtr* section = (IntPtr*)GetModuleSection(moduleManager, ReadyToRunSectionType.ModuleManagerIndirection, out length);
            *section = moduleManager;

            // Initialize strings if any are present
            IntPtr stringSection = GetModuleSection(moduleManager, ReadyToRunSectionType.StringTable, out length);
            if (stringSection != IntPtr.Zero)
            {
                Contract.Assert(length % IntPtr.Size == 0);
                InitializeStringTable(stringSection, length);
            }

            // Initialize statics if any are present
            IntPtr staticsSection = GetModuleSection(moduleManager, ReadyToRunSectionType.GCStaticRegion, out length);
            if (staticsSection != IntPtr.Zero)
            {
                Contract.Assert(length % IntPtr.Size == 0);
                InitializeStatics(staticsSection, length);
            }
        }

        private static unsafe void InitializeEagerClassConstructorsForModule(IntPtr moduleManager)
        {
            int length;

            // Run eager class constructors if any are present
            IntPtr eagerClassConstructorSection = GetModuleSection(moduleManager, ReadyToRunSectionType.EagerCctor, out length);
            if (eagerClassConstructorSection != IntPtr.Zero)
            {
                Contract.Assert(length % IntPtr.Size == 0);
                RunEagerClassConstructors(eagerClassConstructorSection, length);
            }
        }
        
        private static unsafe void InitializeStringTable(IntPtr stringTableStart, int length)
        {
            IntPtr stringTableEnd = (IntPtr)((byte*)stringTableStart + length);
            for (IntPtr* tab = (IntPtr*)stringTableStart; tab < (IntPtr*)stringTableEnd; tab++)
            {
                byte* bytes = (byte*)*tab;
                int len = (int)NativePrimitiveDecoder.DecodeUnsigned(ref bytes);
                int count = LowLevelUTF8Encoding.GetCharCount(bytes, len);
                Contract.Assert(count >= 0);

                string newStr = RuntimeImports.RhNewArrayAsString(EETypePtr.EETypePtrOf<string>(), count);
                fixed (char* dest = newStr)
                {
                    int newCount = LowLevelUTF8Encoding.GetChars(bytes, len, dest, count);
                    Contract.Assert(newCount == count);
                }
                GCHandle handle = GCHandle.Alloc(newStr);
                *tab = (IntPtr)handle;
            }
        }

        private static void Call(System.IntPtr pfn)
        {
        }

        private static unsafe void RunEagerClassConstructors(IntPtr cctorTableStart, int length)
        {
            IntPtr cctorTableEnd = (IntPtr)((byte*)cctorTableStart + length);

            for (IntPtr* tab = (IntPtr*)cctorTableStart; tab < (IntPtr*)cctorTableEnd; tab++)
            {
                Call(*tab);
            }
        }

        private static unsafe void InitializeStatics(IntPtr gcStaticRegionStart, int length)
        {
            IntPtr gcStaticRegionEnd = (IntPtr)((byte*)gcStaticRegionStart + length);
            for (IntPtr* block = (IntPtr*)gcStaticRegionStart; block < (IntPtr*)gcStaticRegionEnd; block++)
            {
                object obj = RuntimeImports.RhNewObject(new EETypePtr(*block));
                *block = RuntimeImports.RhHandleAlloc(obj, GCHandleType.Normal);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe int CStrLen(byte* str)
        {
            int len = 0;
            for (; str[len] != 0; len++) { }
            return len;
        }

        [RuntimeImport(".", "RhpGetModuleSection")]
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern IntPtr GetModuleSection(IntPtr module, ReadyToRunSectionType section, out int length);

        [RuntimeImport(".", "RhpCreateModuleManager")]
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static unsafe extern IntPtr CreateModuleManager(IntPtr moduleHeader);
    }
}
