using System.Runtime.InteropServices;

// System DLLs (dwmapi, user32) only from System32, never from the folder TylerCast.exe was run
// from (a downloaded copy shares Downloads with whatever else was downloaded): security review, finding 6.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
