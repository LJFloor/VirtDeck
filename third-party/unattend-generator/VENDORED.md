# Vendored: cschneegans/unattend-generator

```
Upstream : https://github.com/cschneegans/unattend-generator
Branch   : master
Commit   : 427480c7be47e8bd1301b89747baab38271b3da3
Date     : 2026-08-07
License  : MIT (LICENSE.txt, verbatim)
```

This is the library behind <https://schneegans.de/windows/unattend-generator/>. VirtDeck uses it to
produce the `autounattend.xml` that goes onto the answer disc; see `VirtDeck.Core/Unattend/`.

**Excluded from the copy:** `Example.cs`, `Example.ps1` (samples), `.github/`, `.gitattributes`
(it would change line-ending normalisation for this whole subtree), `.gitignore`, `README.md`.

**Local delta: `UnattendGenerator.csproj` only.** Three changes, each commented in the file itself:

1. Single-targets `net10.0` instead of `net8.0;net9.0;net10.0`. Nothing in `VirtDeck.sln` consumes
   the other two.
2. States `<AssemblyName>` explicitly instead of deriving it from the file name (see below).
3. Turns off `IncludeSourceRevisionInInformationalVersion` and pins `InformationalVersion` to the
   commit above. `modifier/Build.cs` writes the generator's own commit hash and a github.com URL
   into every answer file, reading them out of `AssemblyInformationalVersionAttribute`; the .NET 8+
   SDK has SourceLink built in and would otherwise fill that from **VirtDeck's** git repo, so every
   answer disc would claim a cschneegans commit that does not exist. **Keep the pinned version in
   step with the commit at the top of this file** when updating.

Everything else in this directory is byte-identical to upstream at the commit above, and should stay
that way: a local fix here turns every future update into a merge. If something needs changing,
change it in `VirtDeck.Core/Unattend/` instead, or send it upstream.

The repo-wide "no em dashes or en dashes" rule does **not** apply to this directory. Upstream source
contains typographic punctuation inside PowerShell string literals; verbatim wins.

## Things that will break if you touch them

- **The assembly name is data.** `resource/Bloatware.json` uses Newtonsoft `TypeNameHandling.Auto`
  with `$type` values like `"Schneegans.Unattend.CapabilityBloatwareStep, UnattendGenerator"`.
  Renaming the assembly or the namespace makes `new UnattendGenerator()` throw on first use. It
  fails loudly, but at runtime only, so `dotnet build` will not catch it.
- **For the same reason, this solution can never enable `PublishTrimmed`.** ILLink cannot see types
  that are resolved by name at runtime, so it would strip the bloatware step classes and the failure
  would appear only in the published artifact, never in a dev build.
- **Resources are resolved by logical name** (`Schneegans.Unattend.resource.<file>`), derived from
  the on-disk path plus `RootNamespace`. Do not rename `resource/`, and do not rewrite the
  `EmbeddedResource` item group into a wildcard without checking every name still lands the same.

## Updating

1. `git clone --depth 1 https://github.com/cschneegans/unattend-generator /tmp/ug && git -C /tmp/ug rev-parse HEAD`
2. `rsync -a --delete` the tree over this directory, then delete the excluded files above.
3. `git checkout -- third-party/unattend-generator/UnattendGenerator.csproj` to restore the local
   delta. If upstream changed that file, re-apply the delta by hand instead.
4. Record the new commit and date at the top of this file.
5. `dotnet build VirtDeck.sln`. `VirtDeck.Core/Unattend/UnattendConfigMapper.cs` is the only VirtDeck
   file that compiles against upstream's API; because `Configuration` is a positional record, a
   parameter added upstream shows up there as a compile error. That is the point.
6. **Run the default-XML diff** (below) and read the diff. An upstream change to a default is a
   behaviour change for every answer disc VirtDeck writes, and there is no test suite to catch it.

### The default-XML diff

There is no test project in this repo, so this is a throwaway console app built in the scratchpad and
never committed, referencing this project and `VirtDeck.Core`. It generates the answer file twice and
compares.

VirtDeck's model defaults are upstream's defaults, with **one** deliberate exception: the accounts
page defaults password expiry to Never, where upstream leaves Windows' 42 days. Put that back and the
two must come out **byte-identical**. That equality is the real check, because it covers every one of
the eighty-odd settings at once:

```csharp
var gen = new UnattendGenerator();
var upstream = UnattendGenerator.Serialize(gen.GenerateXml(Configuration.Default));

var neutral = new UnattendConfig();
neutral.UserAccounts.PasswordExpiry = PasswordExpiryMode.WindowsDefault;
var virtdeck = UnattendXml.Build(neutral);

// must be equal; if not, an upstream default moved under us
```

Then diff the *shipped* default (`new UnattendConfig()`) against upstream's and read the result. It
should only ever add, never remove, and at the pinned commit it adds exactly five lines: the
specialize script, its `RunSynchronousCommand`, and the `net.exe accounts /maxpwage:UNLIMITED` inside
it. If that count moves, an upstream update changed what the default emits.

Worth also asserting while you are there, since these are the constraints VirtDeck's UI is built
around and an upstream update could relax or tighten any of them: a table of accounts with no
administrator is refused; a reserved user name is refused; a lockout window longer than the lockout
duration is refused; and "do not logon" generates rather than throwing (see
`UnattendConfigMapper.KeepSensitiveFiles`).
