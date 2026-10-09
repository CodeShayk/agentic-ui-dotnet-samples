# Third-party notice: AG-UI RunAgentInput fixtures

The files under `RunAgentInput/` are copied, unmodified, from the AG-UI protocol repository
(https://github.com/ag-ui-protocol/ag-ui), folder `spec/1.0/fixtures/RunAgentInput/`, at commit `97f789cc`
(2026-10-03). The `valid/` files are inputs the AG-UI 1.0 JSON Schema accepts; each `invalid/` file is one it
rejects, with an `.expect.json` naming the failing keyword and location.

`ConformanceTests.cs` posts every one of them to Beacon's agent: a valid input must produce a stream that
Chapter 3's validator accepts, and an invalid one must be refused before a run starts (with one documented
exception). To refresh them, copy the same folder from a newer commit and note the commit here.

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
