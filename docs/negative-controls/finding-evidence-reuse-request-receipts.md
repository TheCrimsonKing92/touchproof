# Reused finding evidence request receipt negative control

In `ReusedFindingEvidenceRequestReceiptTests.cs`, replace the `FindReuseRecord` expression body with the
old display substring lookup. Keep both typed writers so the mutation isolates receipt recognition.

```csharp
{
    var disposition = decision == FindingEvidenceRequestReceiptDecision.ReusedGreen ? "reused-green" : "reused-red";
    return timeline.LastOrDefault(evt =>
        evt.Message.Contains($"disposition={disposition};", StringComparison.Ordinal));
}
```

`RewordedDisplay_LeavesTypedReceiptUnchanged` should fail at the `Assert.NotNull` after restoring
the reworded timeline, for both reuse decisions. The display no longer contains the substring.
`UnrelatedTextNamingReusePhraseAndGuard_YieldsNoReceipt` should fail at `Assert.Null(fixture.FindReuse(decision))`
for every row. The substring matches both the other task's request and the requesting task's note.

Acceptance owns the focused RED run with this mutation and the GREEN run after restoring the
typed lookup. Use the two named tests in the goal worktree.
