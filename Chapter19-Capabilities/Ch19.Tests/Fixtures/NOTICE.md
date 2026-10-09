# Third-party notice: AG-UI capability fixtures

The files under this folder are copied, unmodified, from the AG-UI protocol repository
(https://github.com/ag-ui-protocol/ag-ui) at commit `97f789cc` (2026-10-03):

| Folder here | Source in the AG-UI repository | What it is |
|---|---|---|
| `AgentCapabilities/` | `spec/1.0/fixtures/AgentCapabilities/` | Whole capability declarations the 1.0 JSON Schema accepts (`valid/`) or rejects (`invalid/`, each with an `.expect.json` naming the failing keyword and location) |
| `ExecutionCapabilities/`, `MultiAgentCapabilities/`, `SubagentInfo/` | the folders of the same name under `spec/1.0/fixtures/` | Single capability groups and records, valid and invalid |

`CapabilityFixtureTests.cs` reads them to check Chapter 19's capability reader against the
specification's own examples. Note the difference between the schema and a consumer: the schema closes
every capability object, so `old-subagents-key.json` is invalid against it, but the capabilities page of
the specification says a consumer MUST NOT reject a declaration for carrying unrecognized members. The
tests expect the reader to accept that document and note the member.

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
