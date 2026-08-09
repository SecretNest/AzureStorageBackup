using System.Runtime.Versioning;

// Keeps the same platform premise as the assembly under test (see the note in src's AssemblyInfo). The
// chmod 000 fixtures here use File.SetUnixFileMode extensively to construct unreadable files, and without
// declaring it this test project alone produces over sixty CA1416 warnings.
[assembly: SupportedOSPlatform("linux")]
