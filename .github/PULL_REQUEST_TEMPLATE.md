## Summary

<!-- What changes, and why. If anything breaks for callers, say so here and how they migrate. -->

## Linked issue

<!-- Open an issue first for anything beyond a small fix, then link it here. Use "Fixes #123". -->

## How this was tested

<!--
Name what you actually ran — CI does not run the test suite, so this section is the only record of it.

  EditMode, in the editor:  Window > General > Test Runner
  EditMode, from a shell:   Unity.exe -batchmode -projectPath <consuming project> \
                              -runTests -testPlatform EditMode -testResults results.xml

Quote the counts. If part of the change was reasoned about rather than run, say which part, and say
what still needs a live backend or dashboard-authored data to confirm.
-->

## Checklist

- [ ] EditMode tests pass locally, and new behaviour has coverage where it makes sense
- [ ] Comments are short one-liners — no banners, no paragraph blocks
- [ ] `README.md` and the matching `Docs~/` page updated if the public API changed
- [ ] Scope is tight — the requested change and its direct dependencies, nothing bundled

<!--
Checked automatically on every PR, so there is nothing to tick by hand for these:
version agreement between package.json and FlockSdkVersion.cs, semver validity, the CHANGELOG entry,
explicit types, and generated-tree hygiene.
-->
