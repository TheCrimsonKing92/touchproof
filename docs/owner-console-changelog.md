# Owner console changelog

Append entries in increasing ID order. Each entry starts with a level-two heading
in the form `## <positive integer> - <one-line summary>`. Follow it with one
`Surface fingerprint: sha256:<64 lowercase hex digits>` line and one or more
`- ` navigation lines. Each navigation line names the keys or command for one
added or changed console view, tab, or command.

The fingerprint covers every binding row in registry order, including dialog rows.
The fields are command, usage, key, Ctrl, scope, effect, owner, confirms, noun,
hint label, key help, and command help. The canonical text joins fields with
U+001F and rows with U+001E. Null is U+0000. Booleans use `True` and `False`,
and enum values use their declared names. Text line endings are normalized to LF.
Literal backslashes and U+0000, U+001E, and U+001F in field values are escaped as
`\\`, `\0`, `\x1e`, and `\x1f`. The digest is SHA-256 over the UTF-8 text.

## 1 - Find Provider connections in Settings.

Surface fingerprint: sha256:a6fb5b7831f11e5c78569860ce0129e2d70635810f7bbc3f8d07044cdf77dafd

- Type `:settings` to open Settings.
- Press Left/Right to switch to Provider connections.
- Type `:news` to read and acknowledge console changes.
