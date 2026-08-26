# Agent Notes

## Localization

Any time you add, remove, rename, or change user-facing text, check localization in the same change.

- Put UI text in `src/OpenLogi.Core/Localization/Strings.resx`, not as raw XAML/C# literals.
- Keep every shipped locale file in sync with the neutral table, including `Strings.de.resx` and `Strings.zh-Hans.resx`.
- When removing or renaming a key, remove or rename it in every locale file too.
- Preserve placeholders exactly, such as `{0}` and `{1}`.
- Run the localization coverage tests after string-table changes:

```sh
dotnet test tests/OpenLogi.Tests/OpenLogi.Tests.csproj --filter "FullyQualifiedName~LocalizationTests|FullyQualifiedName~TranslationCoverageTests"
```
