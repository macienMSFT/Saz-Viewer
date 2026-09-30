# Third-party notices

## SharpZipLib

Package: SharpZipLib 1.4.2

Project: https://github.com/icsharpcode/SharpZipLib

SAZ Viewer uses this fully managed library only for encrypted ZIP/SAZ central-directory inspection and for WinZip AES/PKZIP decryption with integrity verification. Unencrypted archives continue to use the .NET ZIP reader.

The SharpZipLib MIT notice follows:

> Copyright © 2000-2018 SharpZipLib Contributors
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this
> software and associated documentation files (the "Software"), to deal in the Software
> without restriction, including without limitation the rights to use, copy, modify, merge,
> publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
> to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or
> substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
> INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
> PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
> FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
> OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
> DEALINGS IN THE SOFTWARE.

## Office Inspectors for Fiddler

Project: https://github.com/OfficeDev/Office-Inspectors-for-Fiddler

Reference commit: `c18dd66c99f3b5a96c2e1d31698c5cf2deb828e7`

SAZ Viewer independently adapts protocol names, MAPI/HTTP and NSPI field order, MS-OXCRPC extended-buffer framing, auxiliary-buffer framing, ROP identifiers, rule-action layouts, the PidTag/PidLid/string-named property maps, and the Direct2/LZ77 and XOR algorithms from repository-authored source. The implementation replaces the upstream Fiddler, FiddlerCore, WinForms, HexBox, native-marshalling, and global-state architecture with bounded .NET 8 readers and a local HTML report.

The extended-rule action parser intentionally follows the 32-bit EntryID size, recipient count, and property count fields specified by MS-OXORULE sections 2.2.5.1.2.1 through 2.2.5.1.2.4.1 rather than the narrower reads in the pinned upstream source.

The upstream MIT notice follows:

> MIT License
>
> Copyright (c) 2015 Office
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

No upstream third-party binaries or Fiddler/Office integration code are included. In particular, this project does not redistribute `FiddlerCore4.dll`, `Fiddler.exe`, `Be.Windows.Forms.HexBox.dll`, `EQATEC.Analytics.Monitor.dll`, `Newtonsoft.Json.dll`, `ionic.zip.reduced.dll`, Outlook interop assemblies, Visual Studio test binaries, or automation resources.
