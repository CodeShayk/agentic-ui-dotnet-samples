"""Applies steps 1-5 of Appendix C's .NET 10 recipe to a fresh copy of sample-code (scratch only).

Usage: python eng/net10-recipe.py <sample-code> <destination>
"""
import json
import re
import shutil
import sys
from pathlib import Path

src = Path(sys.argv[1])
dst = Path(sys.argv[2])
if dst.exists():
    shutil.rmtree(dst)

skip = {'.dotnet', 'bin', 'obj', 'TestResults', '.vs', 'node_modules'}


def ignore(directory, names):
    return [n for n in names if n in skip]


shutil.copytree(src, dst, ignore=ignore)
print('copied', sum(1 for _ in dst.rglob('*.csproj')), 'projects')

# 1. global.json points at the .NET 10 band.
(dst / 'global.json').write_text(json.dumps({
    'sdk': {'version': '10.0.100', 'rollForward': 'latestFeature'},
    'test': {'runner': 'Microsoft.Testing.Platform'},
}, indent=2) + '\n', encoding='utf-8')

# 2. BeaconTargetFramework.
props = dst / 'Directory.Build.props'
text = props.read_text(encoding='utf-8')
assert '<BeaconTargetFramework>net11.0</BeaconTargetFramework>' in text
props.write_text(text.replace('<BeaconTargetFramework>net11.0</BeaconTargetFramework>',
                              '<BeaconTargetFramework>net10.0</BeaconTargetFramework>'), encoding='utf-8')

# 3. The nine release-candidate pins.
packages = dst / 'Directory.Packages.props'
text = packages.read_text(encoding='utf-8')
n_aspnet = text.count('Version="11.0.0-rc.1.26425.128"')
n_maui = text.count('Version="11.0.0-rc.1.26451.6"')
text = text.replace('Version="11.0.0-rc.1.26425.128"', 'Version="10.0.12"')
text = text.replace('Version="11.0.0-rc.1.26451.6"', 'Version="10.0.110"')
packages.write_text(text, encoding='utf-8')
print('pins replaced:', n_aspnet, '+', n_maui)

# 4. Projects that set their own platform target framework.
platform_projects = []
for csproj in sorted(dst.rglob('*.csproj')):
    text = csproj.read_text(encoding='utf-8')
    if 'net11.0-' in text:
        csproj.write_text(text.replace('net11.0-', 'net10.0-'), encoding='utf-8')
        platform_projects.append(csproj)
print('platform projects:', len(platform_projects))

# 5. Explicit XAML namespaces in the .NET MAUI projects.
maui_ns = 'http://schemas.microsoft.com/dotnet/2021/maui'
x_ns = 'http://schemas.microsoft.com/winfx/2009/xaml'
changed = 0
for csproj in platform_projects:
    if '<UseMaui>true</UseMaui>' not in csproj.read_text(encoding='utf-8'):
        continue
    for xaml in sorted(csproj.parent.rglob('*.xaml')):
        if any(part in ('bin', 'obj') for part in xaml.parts):
            continue
        text = xaml.read_text(encoding='utf-8-sig')
        match = re.search(r'<(?![?!])([\w:.]+)', text)
        if not match:
            continue
        end = text.index('>', match.end())
        tag = text[match.start():end]
        additions = []
        if 'xmlns="' not in tag:
            additions.append(f'xmlns="{maui_ns}"')
        if 'xmlns:x="' not in tag:
            additions.append(f'xmlns:x="{x_ns}"')
        if additions:
            insert_at = match.end()
            text = text[:insert_at] + ' ' + ' '.join(additions) + text[insert_at:]
            xaml.write_text(text, encoding='utf-8')
            changed += 1
print('xaml files given namespaces:', changed)
