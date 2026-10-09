# Third-party notice: AG-UI conformance corpus

The files under this folder are copied, unmodified, from the AG-UI protocol repository
(https://github.com/ag-ui-protocol/ag-ui) at commit `e60019d2` (2026-10-02):

| Folder here | Source in the AG-UI repository | What it is |
|---|---|---|
| `streams/` | `spec/1.0/conformance/streams/` | Event streams with the outcome the specification requires of a consumer (see `spec/1.0/conformance/README.md`) |
| `fixtures/` | `spec/1.0/fixtures/` (the `*Event`, `UserMessage`, `ToolMessage` and `Interrupt` folders) | Single values that the 1.0 JSON Schema accepts (`valid/`) or rejects (`invalid/`, each with an `.expect.json` naming the failing keyword and location) |

The tests in this project (`ConformanceStreamTests.cs`, `SchemaFixtureTests.cs`) read these files to check the
Chapter 3 stream validator against the specification's own examples. To refresh them, copy the same folders
from a newer commit and note the commit here.

The AG-UI repository is distributed under the MIT License:

```text
MIT License

Copyright (c) 2025

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
