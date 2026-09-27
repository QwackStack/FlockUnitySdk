# Compiles the Flock SDK the way the Package Builder ships it with providers left out, so a stripped package that does not
# compile is found here rather than by a studio.
#
# The Package Builder never compiles what it exports: it stages the files, drops the excluded providers' files and folders,
# writes FLOCK_NO_<ID> into a csc.rsp beside the Runtime, Editor and Samples assemblies, and leaving a provider out also
# leaves out every provider that depends on it. This script reads the same manifest (Editor/FlockProviderManifest.cs) and
# stages the same way into a project of its own, then compiles each case with Unity in batchmode.
#
#   python stripped_package_check.py --unity <Unity.exe> --project <empty folder>            every provider, one at a time
#   python stripped_package_check.py --unity <Unity.exe> --project <folder> --case SHOP       one case
#   python stripped_package_check.py ... --case ANALYTICS --keep-dependents                  the check's own control
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EDITOR_VERSION = '6000.3.16f1'
RSP_FOLDERS = ['Runtime', 'Editor', 'Samples/QuickStart']
ROOT_FILES = ['package.json', 'CHANGELOG.md', 'LICENSE.md', 'README.md']
PACKAGES = {
    'com.unity.nuget.newtonsoft-json': '3.2.2',
    'com.unity.modules.androidjni': '1.0.0',
    'com.unity.modules.animation': '1.0.0',
    'com.unity.modules.audio': '1.0.0',
    'com.unity.modules.imageconversion': '1.0.0',
    'com.unity.modules.imgui': '1.0.0',
    'com.unity.modules.jsonserialize': '1.0.0',
    'com.unity.modules.screencapture': '1.0.0',
    'com.unity.modules.ui': '1.0.0',
    'com.unity.modules.uielements': '1.0.0',
    'com.unity.modules.unitywebrequest': '1.0.0',
    'com.unity.modules.unitywebrequestassetbundle': '1.0.0',
    'com.unity.modules.unitywebrequestaudio': '1.0.0',
    'com.unity.modules.unitywebrequesttexture': '1.0.0',
    'com.unity.modules.unitywebrequestwww': '1.0.0',
}
STRING = re.compile('"([^"]+)"')


def read(path):
    with open(os.path.join(REPO, path), encoding='utf-8-sig') as f:
        return f.read()


def strings_in(block, member):
    match = re.search(member + r'\s*=\s*new(?:\s+string)?\s*\[\s*\d*\s*\]\s*\{([^}]*)\}', block)
    return STRING.findall(match.group(1)) if match else []


def providers():
    text = read('Editor/FlockProviderManifest.cs')
    result = {}
    for block in re.split(r'new Entry\s*\{', text)[1:]:
        provider_id = re.search(r'Id\s*=\s*"([A-Z_]+)"', block).group(1)
        result[provider_id] = {
            'files': strings_in(block, 'Files'),
            'folders': [f.replace(chr(92), '/').rstrip('/') + '/' for f in strings_in(block, 'Folders')],
            'depends_on': strings_in(block, 'DependsOn'),
        }
    if len(result) < 2:
        sys.exit('Read %d providers from the manifest; its shape has changed, so this check cannot trust itself.' % len(result))
    return result


def builder_internal_files():
    match = re.search(r'BuilderInternalFiles\s*=\s*\{([^}]*)\}', read('Editor/FlockPackageBuilder.cs'))
    if not match:
        sys.exit('Could not read BuilderInternalFiles from FlockPackageBuilder.cs.')
    return STRING.findall(match.group(1))


def left_out(all_providers, provider_id, keep_dependents):
    # Leaving a provider out leaves out every provider that depends on it, as the builder's toggles do.
    out = {provider_id}
    changed = not keep_dependents
    while changed:
        changed = False
        for other, entry in all_providers.items():
            if other not in out and any(d in out for d in entry['depends_on']):
                out.add(other)
                changed = True
    return out


def stage(project, excluded_ids, all_providers):
    target = os.path.join(project, 'Assets', 'FlockSDK')
    shutil.rmtree(target, ignore_errors=True)
    excluded_files = set()
    excluded_folders = set()
    for f in builder_internal_files():
        excluded_files.update({f, f + '.meta'})
    for provider_id in excluded_ids:
        entry = all_providers[provider_id]
        for f in entry['files']:
            excluded_files.update({f, f + '.meta'})
        excluded_folders.update(entry['folders'])

    def excluded(rel):
        if rel in excluded_files:
            return True
        lower = rel.lower()
        return any(lower.startswith(folder.lower()) or lower == folder.rstrip('/').lower() + '.meta' for folder in excluded_folders)

    copied = 0
    for top in ['Runtime', 'Editor', 'Samples']:
        for root, _, files in os.walk(os.path.join(REPO, top)):
            for name in files:
                rel = os.path.relpath(os.path.join(root, name), REPO).replace(chr(92), '/')
                if excluded(rel):
                    continue
                destination = os.path.join(target, rel)
                os.makedirs(os.path.dirname(destination), exist_ok=True)
                shutil.copyfile(os.path.join(REPO, rel), destination)
                copied += 1
        if os.path.exists(os.path.join(REPO, top + '.meta')):
            shutil.copyfile(os.path.join(REPO, top + '.meta'), os.path.join(target, top + '.meta'))
    for f in ROOT_FILES:
        if os.path.exists(os.path.join(REPO, f)):
            shutil.copyfile(os.path.join(REPO, f), os.path.join(target, f))
    defines = sorted('FLOCK_NO_' + provider_id for provider_id in excluded_ids)
    for folder in RSP_FOLDERS:
        rsp = os.path.join(target, folder, 'csc.rsp')
        if defines and os.path.isdir(os.path.dirname(rsp)):
            with open(rsp, 'w', encoding='utf-8', newline='\n') as f:
                f.write(''.join('-define:%s\n' % d for d in defines))
    return copied


def make_project(project):
    os.makedirs(os.path.join(project, 'Packages'), exist_ok=True)
    os.makedirs(os.path.join(project, 'ProjectSettings'), exist_ok=True)
    with open(os.path.join(project, 'Packages', 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump({'dependencies': PACKAGES}, f, indent=2)
    with open(os.path.join(project, 'ProjectSettings', 'ProjectVersion.txt'), 'w', encoding='utf-8') as f:
        f.write('m_EditorVersion: %s\n' % EDITOR_VERSION)


def compile_errors(unity, project, log):
    if os.path.exists(log):
        os.remove(log)
    started = time.time()
    subprocess.run([unity, '-batchmode', '-quit', '-nographics', '-projectPath', project, '-logFile', log])
    if not os.path.exists(log):
        sys.exit('Unity wrote no log; nothing was compiled, so nothing is proven.')
    with open(log, encoding='utf-8', errors='replace') as f:
        errors = sorted(set(line.strip() for line in f if 'error CS' in line))
    # Positive control: with no errors, the staged runtime must have been compiled in this run, or "no errors" means nothing.
    runtime = os.path.join(project, 'Library', 'ScriptAssemblies', 'Flock.Runtime.dll')
    if not errors and (not os.path.exists(runtime) or os.path.getmtime(runtime) < started):
        sys.exit('No errors, but Flock.Runtime.dll was not built in this run; the staged package was not compiled.')
    return errors


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--unity', required=True)
    parser.add_argument('--project', required=True)
    parser.add_argument('--case', action='append', help='a provider id to leave out, NONE for the whole package or ALL for none of the providers; repeatable')
    parser.add_argument('--keep-dependents', action='store_true', help='leave dependents in: the check\'s own control, which must fail')
    args = parser.parse_args()

    all_providers = providers()
    cases = args.case or (['NONE'] + sorted(all_providers) + ['ALL'])
    project = os.path.abspath(args.project)
    make_project(project)
    failed = 0
    for case in cases:
        if case not in ('NONE', 'ALL') and case not in all_providers:
            sys.exit('No provider %s in the manifest (it has %s).' % (case, ', '.join(sorted(all_providers))))
        if case == 'NONE':
            excluded = set()
        elif case == 'ALL':
            excluded = set(all_providers)
        else:
            excluded = left_out(all_providers, case, args.keep_dependents)
        copied = stage(project, excluded, all_providers)
        errors = compile_errors(args.unity, project, os.path.join(project, 'compile-%s.log' % case))
        label = '%s (left out: %s; %d files)' % (case, ', '.join(sorted(excluded)) or 'nothing', copied)
        if errors:
            failed += 1
            print('FAIL %s: %d errors' % (label, len(errors)))
            for line in errors[:20]:
                print('    ' + line)
        else:
            print('PASS %s' % label)
        sys.stdout.flush()
    shutil.rmtree(os.path.join(project, 'Assets', 'FlockSDK'), ignore_errors=True)
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
