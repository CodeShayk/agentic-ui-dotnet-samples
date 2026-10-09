# JSON Patch Recipes

**An online appendix to *AG-UI in .NET*.** The book's chapters refer to it as the online *JSON Patch Recipes*; its code is in [`Online-JsonPatch-Recipes/`](../Online-JsonPatch-Recipes/), and every path below is relative to the root of the companion repository.

*Chapter 12 settled the rules for shared state: a `STATE_DELTA` is an RFC 6902 patch against the document the client holds, it applies completely or not at all, and a `test` turns a shifted index into a refused patch instead of a wrong one. This appendix turns those rules into recipes: operations and pointers, patches from intent and from a diff, typed patches on the server, applying patches on the client, choosing between a snapshot and a delta and testing. Each recipe is a compiled listing followed by the patch it produces.*

> **Companion code:** [`Online-JsonPatch-Recipes/`](../Online-JsonPatch-Recipes/) contains [`JsonPatchRecipes`](../Online-JsonPatch-Recipes/JsonPatchRecipes/), a class library with one file per recipe, and [`JsonPatchRecipes.Tests`](../Online-JsonPatch-Recipes/JsonPatchRecipes.Tests/), which applies every patch the server-side recipes build with `JsonPatch.Apply` from [`Shared/Beacon.Client.Core`](../Shared/Beacon.Client.Core/), the applier Beacon's clients use. From the repository root (the book's `sample-code` folder), run the tests with
> `dotnet test --project Online-JsonPatch-Recipes/JsonPatchRecipes.Tests`. With `JSON_PATCH_CAPTURE_DIR` set to a folder, they also write each recipe's output there; the outputs printed here are those files.

The recipes work on Chapter 12's sheet for WO-1001 with one addition, a `readings` object holding what Priya measured across the dual run capacitor's terminals, in microfarads: `{"C/HERM": 38.0, "C/FAN": 4.9}`, the 38 µF against a 45 µF rating that makes CAP-45-5 the culprit. They build operations as `JsonObject`s and hand each patch to AG-UI as the `JsonElement` that `StateDeltaEvent.Delta` carries; Chapter 12 and the online *AGUIStreamOptions Cookbook* show how that element reaches the wire.

## 1 Operations and Pointers

RFC 6902 defines six operations, and three of them (`add`, `replace` and `test`) need a `value`. Listing 1 builds all six.

**Listing 1** — One helper per operation, and the element a `STATE_DELTA` carries ([`Online-JsonPatch-Recipes/JsonPatchRecipes/PatchOps.cs`](../Online-JsonPatch-Recipes/JsonPatchRecipes/PatchOps.cs))

```csharp
public static class PatchOps
{
    public static JsonObject Add(string path, JsonNode? value) => WithValue("add", path, value);

    public static JsonObject Replace(string path, JsonNode? value) => WithValue("replace", path, value);

    public static JsonObject Test(string path, JsonNode? value) => WithValue("test", path, value);

    public static JsonObject Remove(string path) => new() { ["op"] = "remove", ["path"] = path };

    public static JsonObject Move(string from, string path) => new() { ["op"] = "move", ["from"] = from, ["path"] = path };

    public static JsonObject Copy(string from, string path) => new() { ["op"] = "copy", ["from"] = from, ["path"] = path };

    /// <summary>The patch as the value of a <c>STATE_DELTA</c>'s <c>delta</c>.</summary>
    public static JsonElement ToElement(JsonArray patch) =>
        JsonSerializer.SerializeToElement(patch, RecipesJsonContext.Default.JsonArray);

    // add, replace and test require "value", and JSON null is a legitimate value, so the member is always
    // written: { "op": "replace", "path": "/x", "value": null } sets x to null. The node is copied because
    // a JsonNode can belong to only one parent.
    private static JsonObject WithValue(string op, string path, JsonNode? value) =>
        new() { ["op"] = op, ["path"] = path, ["value"] = value?.DeepClone() };
}
```

The `value` member is written even when it holds `null`, because `{"op":"add","path":"/workOrder/eta","value":null}` adds a member that is null, while the same operation without `value` is malformed. AG-UI's rule that optional fields are omitted rather than sent as `null` covers protocol fields, never the data inside a patch. The value is cloned because a `JsonNode` belongs to exactly one parent.

Paths are RFC 6901 JSON Pointers, and member names are data. Written into a path as it stands, the reading `C/HERM` becomes two segments, a member `C` holding a member `HERM`, so Listing 2 escapes every segment.

**Listing 2** — The two escapes, in the order RFC 6901 requires ([`Online-JsonPatch-Recipes/JsonPatchRecipes/JsonPointer.cs`](../Online-JsonPatch-Recipes/JsonPatchRecipes/JsonPointer.cs))

```csharp
    /// <summary>
    /// Escapes one reference token: <c>~</c> becomes <c>~0</c>, then <c>/</c> becomes <c>~1</c>. Escaping
    /// <c>/</c> first would escape the tilde of every <c>~1</c> it had just written.
    /// </summary>
    public static string Escape(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>
    /// Decodes one reference token in the opposite order: <c>~1</c> first, then <c>~0</c>, so that
    /// <c>~01</c> decodes to the two characters <c>~1</c> and not to <c>/</c>.
    /// </summary>
    public static string Unescape(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
```

The order is the whole trick: escaping `~` first means the `~1` written for a slash is never escaped again, and decoding `~1` first means `~01` comes back as the two characters `~1`, not as a slash. Priya's reading of the new capacitor, addressed with `JsonPointer.Create("workOrder", "readings", "C/HERM")`, is Output 1.

**Output 1** — A reading addressed through its escaped name

```json
[{"op":"replace","path":"/workOrder/readings/C~1HERM","value":44.6}]
```

Unescaped, the same `replace` names a location that doesn't exist, and `JsonPatch.Apply` refuses it; on a document that happened to contain a `C` object, it would write somewhere else. Note also that `-` means "after the last element" only at the end of an array path; in an object it's an ordinary member name. `JsonPointer.IsValid` checks a pointer against the 1.0 schema's pattern, with `\z` for `$` because .NET's `$` also matches before a trailing line break.

## 2 Patches from Intent

An agent's tool knows what it means to do, and a patch built from that intent is small and can pin exactly what the intent depends on. Appending needs no pin at all: `-` lands after whatever the client's array holds, so a concurrent removal can't misplace the new item (Output 2).

**Output 2** — Appending an item (`ChecklistPatches.Add`)

```json
[{"op":"add","path":"/workOrder/checklist/-","value":{"id":"B1","text":"Replace run capacitor (CAP-45-5)","done":false}}]
```

Ticking addresses an item by index, and an index is a position, not an identity, so Listing 3 tests the item's `id` before it touches `done`.

**Listing 3** — A tick that can't land on the wrong item ([`Online-JsonPatch-Recipes/JsonPatchRecipes/ChecklistPatches.cs`](../Online-JsonPatch-Recipes/JsonPatchRecipes/ChecklistPatches.cs))

```csharp
    public static JsonArray? SetDone(JsonNode? sheet, string itemId, bool done)
    {
        var index = IndexOf(sheet, itemId);
        if (index < 0)
        {
            return null;
        }

        if ((Items(sheet)![index] as JsonObject)?["done"]?.GetValueKind() == (done ? JsonValueKind.True : JsonValueKind.False))
        {
            return [];
        }

        var item = JsonPointer.Append(ChecklistPath, index);
        return
        [
            PatchOps.Test(JsonPointer.Append(item, "id"), itemId),
            PatchOps.Replace(JsonPointer.Append(item, "done"), done),
        ];
    }
```

**Output 3** — What `SetDone(sheet, "C3", done: true)` builds

```json
[{"op":"test","path":"/workOrder/checklist/2/id","value":"C3"},{"op":"replace","path":"/workOrder/checklist/2/done","value":true}]
```

The return values carry meaning, as in Chapter 12's `SheetSession`. An empty patch means the item is already in that state; the AG-UI schema calls `[]` a valid no-op, but there's no reason to send one. `null` means there's no such item to pin, so the caller sends a snapshot or nothing.

Reordering is where intent earns its keep, because RFC 6902 performs a `move` as a `remove` at `from` followed by an `add` at `path`, so `path` indexes the array *without* the moved item. To put C1 after C3, the obvious pointer is C3's index plus one, `/workOrder/checklist/3`; but once C1 is removed, index 3 is the end of the list, and C1 lands one place late, after C4. Listing 4 computes the index after the removal and pins both identities, the item's and its anchor's.

**Listing 4** — Moving an item after another one ([`Online-JsonPatch-Recipes/JsonPatchRecipes/ChecklistPatches.cs`](../Online-JsonPatch-Recipes/JsonPatchRecipes/ChecklistPatches.cs))

```csharp
    public static JsonArray? MoveAfter(JsonNode? sheet, string itemId, string? afterItemId)
    {
        var from = IndexOf(sheet, itemId);
        var anchor = afterItemId is null ? -1 : IndexOf(sheet, afterItemId);
        if (from < 0 || (afterItemId is not null && anchor < 0) || itemId == afterItemId)
        {
            return null;
        }

        // RFC 6902 performs a move as a remove followed by an add, so "path" indexes the array *without*
        // the item. An anchor earlier in the list keeps its index when the item is removed; a later one drops by one.
        var to = anchor < 0 ? 0 : anchor < from ? anchor + 1 : anchor;
        if (to == from)
        {
            return [];
        }

        JsonArray patch = [PatchOps.Test(JsonPointer.Append(JsonPointer.Append(ChecklistPath, from), "id"), itemId)];
        if (anchor >= 0)
        {
            patch.Add(PatchOps.Test(JsonPointer.Append(JsonPointer.Append(ChecklistPath, anchor), "id"), afterItemId));
        }

        patch.Add(PatchOps.Move(JsonPointer.Append(ChecklistPath, from), JsonPointer.Append(ChecklistPath, to)));
        return patch;
    }
```

**Output 4** — C1 after C3

```json
[{"op":"test","path":"/workOrder/checklist/0/id","value":"C1"},{"op":"test","path":"/workOrder/checklist/2/id","value":"C3"},{"op":"move","from":"/workOrder/checklist/0","path":"/workOrder/checklist/2"}]
```

If Dana dragged C3 to the top while the agent worked, the second `test` fails and the move is refused, which is right: the intent was relative to C3, and C3 has moved. `ChecklistPatches.Remove` has the same shape, a `test` of the `id` and a `remove`.

## 3 Patches from a Before/After Diff

Sometimes the server knows the result but not the intent: a workflow step returns a new sheet, or a service recomputes the document. Then you diff the document the client holds against the one it should hold. Listing 5 is the array case of `JsonDiff`.

**Listing 5** — Arrays are compared position by position ([`Online-JsonPatch-Recipes/JsonPatchRecipes/JsonDiff.cs`](../Online-JsonPatch-Recipes/JsonPatchRecipes/JsonDiff.cs))

```csharp
            case (JsonArray was, JsonArray now):
                var common = Math.Min(was.Count, now.Count);
                for (var i = 0; i < common; i++)
                {
                    Diff(was[i], now[i], JsonPointer.Append(path, i), patch, guard);
                }

                // Remove surplus elements from the end, so no removal shifts an index a later one uses.
                for (var i = was.Count - 1; i >= now.Count; i--)
                {
                    Remove(JsonPointer.Append(path, i), was[i], patch, guard);
                }

                for (var i = was.Count; i < now.Count; i++)
                {
                    patch.Add(PatchOps.Add(JsonPointer.Append(path, JsonPointer.End), now[i]));
                }

                break;
```

Objects are compared member by member, removed members first, and values of different kinds, or different scalars, become a `replace`. Output 5 is the diff for a run that ticked C3, added a note and set the status to `InProgress`.

**Output 5** — A plain diff, one operation per line where the file has one line (`JsonDiff.Create(before, after)`)

```json
[{"op":"replace","path":"/workOrder/status","value":"InProgress"},
{"op":"replace","path":"/workOrder/checklist/2/done","value":true},
{"op":"add","path":"/workOrder/notes/-","value":"Run capacitor reads 15.6% below its rating."}]
```

A diff is exact about values and blind to identity. Insert one item at the top of the checklist and every item below compares unequal with its new neighbor: the diff rewrites the `id` and `text` of all four and re-adds the last one, nine operations where the intent was one `add` at `/workOrder/checklist/0`. A plain diff also overwrites whatever the client has at the paths it changes, so if Dana put the job on hold meanwhile, Output 5 silently sets her status back to `InProgress`.

The `guard` flag deals with that. With it, every `replace` and `remove` is preceded by a `test` of the value it overwrites (Output 6), so the patch applies only where the client still holds what the server thinks it holds. On Dana's copy the first `test` fails, nothing applies and her client resynchronizes from the next snapshot.

**Output 6** — The same diff, guarded, one operation per line (`JsonDiff.Create(before, after, guard: true)`)

```json
[{"op":"test","path":"/workOrder/status","value":"Scheduled"},
{"op":"replace","path":"/workOrder/status","value":"InProgress"},
{"op":"test","path":"/workOrder/checklist/2/done","value":false},
{"op":"replace","path":"/workOrder/checklist/2/done","value":true},
{"op":"add","path":"/workOrder/notes/-","value":"Run capacitor reads 15.6% below its rating."}]
```

One gap belongs to JSON Patch itself: no operation tests that a member is absent, and an `add` replaces any value already there. So a guarded diff can't protect an `add` of a new member: if the client added the same member meanwhile, the server's value silently wins. Things both sides may create need IDs from separate namespaces, as Chapter 12's `B1` and `D1` are.

## 4 Typed Patches with `Microsoft.AspNetCore.JsonPatch.SystemTextJson`

A server that keeps its state as records can build patches from expressions with `JsonPatchDocument<T>` from `Microsoft.AspNetCore.JsonPatch.SystemTextJson`, which ships with each .NET release (the book's Appendix C lists the pinned version). Listing 6 is the tick again.

**Listing 6** — The tick, typed ([`Online-JsonPatch-Recipes/JsonPatchRecipes/TypedPatches.cs`](../Online-JsonPatch-Recipes/JsonPatchRecipes/TypedPatches.cs))

```csharp
    // Reflection-based on purpose: the package's converter isn't accessible to the System.Text.Json source
    // generator (SYSLIB1220), so a JsonPatchDocument can't join a source-generated context.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(RecipesJsonContext.Default, new DefaultJsonTypeInfoResolver()),
    };

    /// <summary>Ticks or clears the item at <paramref name="index"/>, pinned to its ID.</summary>
    public static JsonPatchDocument<Sheet> SetDone(int index, string itemId, bool done)
    {
        // The document's options name the path segments: with the camelCase Options above,
        // s => s.WorkOrder.Checklist[index].Done becomes /workOrder/checklist/2/done.
        var patch = new JsonPatchDocument<Sheet>([], Options);
        patch.Test(s => s.WorkOrder.Checklist[index].Id, itemId);
        patch.Replace(s => s.WorkOrder.Checklist[index].Done, done);
        return patch;
    }

    /// <summary>
    /// The patch as the value of a <c>STATE_DELTA</c>'s <c>delta</c>. Serialize with the same options the
    /// document was created with: those named the paths, these name the members inside each value.
    /// </summary>
    public static JsonElement ToElement(JsonPatchDocument<Sheet> patch) =>
        JsonSerializer.SerializeToElement(patch, Options);
```

**Output 7** — The typed document, serialized

```json
[{"value":"C3","path":"/workOrder/checklist/2/id","op":"test"},{"value":true,"path":"/workOrder/checklist/2/done","op":"replace"}]
```

The members come out as `value`, `path` and `op`, which is fine because JSON objects are unordered, and the client applies the patch the same way it applies Output 3. Two sets of options shape it, though. The document's options name the path segments: without a naming policy, `s => s.WorkOrder.Status` becomes `/WorkOrder/Status`, which the client can't resolve. The serializer's options name the members inside each `value`, so an added `ChecklistEntry` serialized with default options arrives as `{"Id":...}` under a camelCase path. Use one options instance for both, and note the comment at the top: the package's converter isn't accessible to the System.Text.Json source generator (SYSLIB1220), so it serializes through reflection.

Reflection doesn't belong in `Beacon.Client.Core`, which is built with `IsAotCompatible`. That is a second reason for the clients' own applier, beside the one Chapter 7 gave: the package declares a framework reference to `Microsoft.AspNetCore.App`, which .NET MAUI and Blazor WebAssembly apps can't take. On an ASP.NET Core server none of this matters.

## 5 Applying Patches on the Client

`JsonPatch.Apply(JsonNode? document, JsonElement patch)` patches a copy of the document and returns a `JsonPatchResult`: the patched copy, or the original instance with an `Error`, plus `Warnings` for skipped operations. Chapter 7's reducer calls it for every `STATE_DELTA`. Code that applies state events outside `AgentConversation`, such as a console tool or a test harness, needs the same discipline, and `StateReplica` packages it (Listing 7).

**Listing 7** — A replica that keeps the three failures apart ([`Online-JsonPatch-Recipes/JsonPatchRecipes/StateReplica.cs`](../Online-JsonPatch-Recipes/JsonPatchRecipes/StateReplica.cs))

```csharp
    private StateOutcome Replace(JsonElement snapshot)
    {
        // Whatever the replica skipped before is spent: a snapshot is the new baseline.
        Document = JsonNode.Parse(snapshot.GetRawText());
        NeedsResync = false;
        return StateOutcome.Replaced;
    }

    private StateOutcome Patch(JsonElement delta)
    {
        if (PatchShape.Check(delta).Problem is { } problem)
        {
            Warnings.Add($"STATE_DELTA is malformed: {problem}.");
            return StateOutcome.Malformed;
        }

        var result = JsonPatch.Apply(Document, delta); // atomic: a copy is patched, the original is untouched
        Warnings.AddRange(result.Warnings);
        if (!result.Succeeded)
        {
            Warnings.Add($"STATE_DELTA didn't apply, so the state kept its prior value: {result.Error}");
            NeedsResync = true;
            return StateOutcome.Skipped;
        }

        Document = result.Document;
        return StateOutcome.Patched;
    }
```

The specification separates three failures. A malformed patch (not an array, an operation missing a required member, a path that isn't a JSON Pointer) is fatal, so `StateOutcome.Malformed` tells the caller to treat the run as failed; `PatchShape.Check` decides that from structure alone. An `op` that RFC 6902 doesn't define is unrecognized rather than malformed, so `JsonPatch.Apply` drops that operation with a warning and applies the rest. And a well-formed patch that doesn't apply is skipped: the document keeps its prior value, `NeedsResync` goes up and the next `STATE_SNAPSHOT` clears it. `Beacon.Client.Core`'s reducer reads it the same strict way (Chapter 7). `StateReplica.BeginRun` holds one more rule: a run's deltas apply to the state its input carried, or to `{}`.

Atomicity has two halves. On the client it means all or nothing: `JsonPatch.Apply` works on a deep copy, so after a failure you get back the very instance you passed in, which `ReplicaTests` checks with `Assert.Same`. On the server it's a matter of packaging, because each `STATE_DELTA` is its own unit. A `test` guards only operations in its own delta, so Output 3 split across two events guards nothing, and a delta that assumes an earlier one applied inherits its failure. Send one intent per delta, pin what it depends on and end a run that sent deltas with a snapshot, as Chapter 12 does.

## 6 Snapshot or Delta?

A delta saves bytes; a snapshot can't fail. The specification lets a producer send a snapshot at any time and asks for one whenever it can't guarantee the consumer's baseline. Listing 8 turns that into a decision.

**Listing 8** — Choosing the event ([`Online-JsonPatch-Recipes/JsonPatchRecipes/StateUpdates.cs`](../Online-JsonPatch-Recipes/JsonPatchRecipes/StateUpdates.cs))

```csharp
    public static BaseEvent? Next(JsonNode? before, JsonNode? after, bool baselineKnown)
    {
        if (baselineKnown && JsonNode.DeepEquals(before, after))
        {
            return null;
        }

        if (!baselineKnown || before is not (JsonObject or JsonArray) || before.GetValueKind() != after?.GetValueKind())
        {
            return Snapshot(after);
        }

        // A guarded patch refuses to apply to a document that has drifted, which turns divergence into a
        // skipped delta and a resync instead of a silently wrong document.
        var patch = JsonDiff.Create(before, after, guard: true);
        var patchBytes = Utf8Length(PatchOps.ToElement(patch));
        var snapshotBytes = Utf8Length(ToElement(after));

        return patchBytes * 2 > snapshotBytes
            ? Snapshot(after)
            : new StateDeltaEvent { Delta = PatchOps.ToElement(patch) };
    }
```

Nothing changed means nothing to send. A baseline the server can't vouch for, after an error, after a delta that may have failed or after a schema change, means a snapshot, and so does a change of kind at the root. Otherwise the guarded diff competes on size. For the tick, the delta is 133 bytes against a 436-byte sheet; for the insertion at the top, the guarded diff is 17 operations and 1,335 bytes against a 482-byte snapshot (the tests write both comparisons to `tick-sizes.json` and `sizes.json`). The factor of two is my judgment, not a rule: a few hundred extra bytes cost less than a resync. Intent patches rarely lose this contest, one more reason to prefer them when the intent is known.

## 7 Testing Patches

Patches fail quietly, so they deserve three kinds of test. Golden tests pin the operations a recipe builds, member by member through `JsonDocument` (the tests' `Sheets.AssertOp`). Concurrency tests apply a patch to a copy someone else changed, such as Dana's sheet without C1, and assert that it fails and leaves the same instance. And generated round trips check the property a diff must have: applying `JsonDiff.Create(before, after)` to `before` gives `after` (Listing 9).

**Listing 9** — Two hundred seeded round trips ([`Online-JsonPatch-Recipes/JsonPatchRecipes.Tests/RoundTripTests.cs`](../Online-JsonPatch-Recipes/JsonPatchRecipes.Tests/RoundTripTests.cs))

```csharp
    // Member names that exercise the pointer escapes and the tokens that look special.
    private static readonly string[] Names = ["id", "done", "notes", "C/HERM", "a~b", "~1", "-", "0", ""];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 200)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Applying_a_diff_reproduces_the_document_it_was_computed_from(int seed)
    {
        var random = new Random(seed);
        var before = RandomObject(random, depth: 0);
        var after = Mutate(before, random, edits: 1 + random.Next(6));

        foreach (var guard in (bool[])[false, true])
        {
            var patch = PatchOps.ToElement(JsonDiff.Create(before, after, guard));
            Assert.False(PatchShape.Check(patch).IsMalformed, $"seed {seed}");

            var result = JsonPatch.Apply(before, patch);
            Assert.True(result.Succeeded, $"seed {seed}, guard {guard}: {result.Error}");
            Assert.True(JsonNode.DeepEquals(after, result.Document), $"seed {seed}, guard {guard}");
        }
    }
```

The seeds make a failure replayable, and the member names are chosen to break careless code: a slash, a tilde, `~1`, `-`, `0` and the empty string. When I swapped the two replacements in `JsonPointer.Escape`, 62 of the 250 tests failed, most of them generated cases. A last test sends a patch through the SDK, a `StateDeltaEvent` serialized with `AGUIJsonSerializerContext`, read back and folded into a view by Chapter 7's `ConversationReducer`, and checks that the `null` value of its last operation survives (Output 8).

**Output 8** — A delta as `AGUIJsonSerializerContext` writes it

```json
{"type":"STATE_DELTA","delta":[{"op":"test","path":"/workOrder/checklist/2/id","value":"C3"},{"op":"replace","path":"/workOrder/checklist/2/done","value":true},{"op":"add","path":"/workOrder/eta","value":null}]}
```

```text
Test run summary: Passed!
  total: 250
  failed: 0
  succeeded: 250
  skipped: 0
```

With these recipes a server can say exactly what it changed and pin what the change depends on, and a client can tell a patch it must refuse from one it merely couldn't apply.

## Further Reading

- [RFC 6902: JavaScript Object Notation (JSON) Patch](https://www.rfc-editor.org/rfc/rfc6902) — The six operations, `move` as a remove followed by an add, `-` and the rule that members an operation doesn't define are ignored.
- [RFC 6901: JavaScript Object Notation (JSON) Pointer](https://www.rfc-editor.org/rfc/rfc6901) — Reference tokens, the `~0` and `~1` escapes and the order in which to decode them.
- [Snapshots and deltas (spec 1.0)](https://docs.ag-ui.com/spec/1.0/basic/patterns/snapshots) — Atomic application, malformed versus unrecognized versus inapplicable patches and each side's duty to resynchronize.
- [State events (spec 1.0)](https://docs.ag-ui.com/spec/1.0/events/state) — The baseline a `STATE_DELTA` applies to, and state as remote writes to validate.
- [Schema reference: JsonPatch (spec 1.0)](https://docs.ag-ui.com/spec/1.0/schema#jsonpatch) — The operation shapes, why they're open objects and the pointer pattern `JsonPointer.IsValid` uses.
- [JSON Patch in ASP.NET Core](https://learn.microsoft.com/aspnet/core/web-api/jsonpatch) — `Microsoft.AspNetCore.JsonPatch.SystemTextJson` and `JsonPatchDocument<T>` on the server.
- [Use a JSON DOM with System.Text.Json](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/use-dom) — `JsonNode`, `DeepClone` and `DeepEquals`, which every recipe here relies on.
