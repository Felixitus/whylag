# Third-party notices

WhyLag is licensed under the Apache License 2.0 (see [LICENSE](LICENSE)). Release packages contain the following
third-party components.

## .NET runtime and libraries

Release builds are self-contained and include the .NET runtime, WPF and Windows Forms, and
`System.Diagnostics.EventLog`, © .NET Foundation and Contributors, licensed under the MIT license.
Source: <https://github.com/dotnet/runtime>, <https://github.com/dotnet/wpf>, <https://github.com/dotnet/winforms>.

## Intel PresentMon

The installer and the portable package include `tools/PresentMon-2.6.0-x64.exe`, unmodified, from
<https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0>. WhyLag starts it as a separate process to capture
frame times.

```text
Copyright (C) 2017-2024 Intel Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included
in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL
THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE
OR OTHER DEALINGS IN THE SOFTWARE.
```

## AI models

WhyLag does not include any AI model. Models downloaded through Ollama are subject to their authors' licenses, which
WhyLag shows before a download. Ollama itself is a separate program and is not distributed with WhyLag.
