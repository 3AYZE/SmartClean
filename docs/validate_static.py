"""Static source checks for hosts without the Windows/WinUI build toolchain."""
import re
from pathlib import Path
from lxml import etree

project = Path(__file__).resolve().parents[1]
app_xaml = project / 'src/SmartClean.WinUI/App.xaml'
main_xaml = project / 'src/SmartClean.WinUI/MainWindow.xaml'
cs = (project / 'src/SmartClean.WinUI/MainWindow.xaml.cs').read_text()
core = list((project / 'src/SmartClean.Core').glob('*.cs'))
assert len(core) >= 6, f'Missing core files: {len(core)}'

for f in [app_xaml, main_xaml, *project.rglob('*.csproj')]:
    etree.parse(str(f))
    print('XML OK:', f.relative_to(project))

root = etree.parse(str(main_xaml)).getroot()
ns = {'x':'http://schemas.microsoft.com/winfx/2006/xaml'}
all_named = {node.attrib[f'{{{ns["x"]}}}Name'] for node in root.iter()
             if f'{{{ns["x"]}}}Name' in node.attrib}
required_names = ['RootGrid','Nav','TitleBarArea','StatusInfo','ScanRing','ScanStatusText',
                  'CancelButton','RefreshButton','DiskBar','DiskSizeText','DiskFreeText',
                  'AppsCount','ProtectedCount','DependencyCount','StorageSummary',
                  'OverviewView','AppsView','FoldersView','SafetyView','SettingsView',
                  'AppsSearch','AppList','FolderList','AppListSubtitle','WarningsList',
                  'DetailName','DetailPublisher','DetailProtection','DetailDependencies','DetailLocation',
                  'ThemePicker']
assert set(required_names).issubset(all_named), sorted(set(required_names)-all_named)
for name in required_names:
    assert re.search(rf'\b{re.escape(name)}\b',cs), 'Missing code reference: '+name

handlers = {value for element in root.iter() for attr,value in element.attrib.items()
            if attr in ('Click','SelectionChanged','TextChanged')}
for handler in handlers:
    assert re.search(r'\b'+re.escape(handler)+r'\s*\(',cs), 'Missing handler '+handler
    print('EVENT OK:',handler)
for f in core:
    content=f.read_text()
    for signature in (r'File\.Delete\s*\(',r'Directory\.Delete\s*\(',r'Process\.Start\s*\(',
                      r'RegistryKey\.DeleteSubKey\s*\('):
        assert not re.search(signature,content), f'Potential destructive API in {f.name}'

readme=(project/'README.md').read_text()
assert 'read-only' in readme and 'No cleanup/removal operations' in readme
assert 'WindowsAppSDKSelfContained' in (project/'src/SmartClean.WinUI/SmartClean.WinUI.csproj').read_text()
assert '2.5.1' in (project/'src/SmartClean.WinUI/SmartClean.WinUI.csproj').read_text()
ui_proj = (project/'src/SmartClean.WinUI/SmartClean.WinUI.csproj').read_text()
for key, value in {'WindowsPackageType':'None', 'WindowsAppSDKSelfContained':'true', 'EnableMsixTooling':'true', 'PublishTrimmed':'false', 'PublishAot':'false', 'PublishSingleFile':'false'}.items():
    assert f'<{key}>{value}</{key}>' in ui_proj, f'Missing WinUI publish prerequisite {key}'
assert 'MainWindow XAML initialized' in cs and 'MainWindow constructor entered' in cs
assert '-p:EnableMsixTooling=true' in (project/'Build-Windows.cmd').read_text()
assert (project/'Run-SmartClean-Diagnostics.cmd').exists()
assert (project/'Run-SmartClean-Diagnostics.ps1').exists()
assert (project/'src/SmartClean.WinUI/StartupDiagnostics.cs').exists()
assert 'StartupDiagnostics.Report' in (project/'src/SmartClean.WinUI/App.xaml.cs').read_text()
assert (project/'Build-Windows.cmd').exists()
assert (project/'NuGet.Config').exists()
config = etree.parse(str(project/'NuGet.Config'))
assert config.xpath('/configuration/packageSources/add[@key="nuget.org"][@value="https://api.nuget.org/v3/index.json"]')
assert config.xpath('/configuration/packageSources/clear')
assert not etree.parse(str(project/'src/SmartClean.WinUI/SmartClean.WinUI.csproj')).xpath('/Project/PropertyGroup/RuntimeIdentifiers'), 'Do not restore x64 and arm64 together'

for csproj in project.rglob('*.csproj'):
    xml = csproj.read_text()
    assert '<SmartCleanTargetFramework' in xml, f'Missing configurable TFM in {csproj}'
    assert '$(SmartCleanTargetFramework)' in xml, f'Missing TFM reference in {csproj}'
cmd = (project / 'Build-Windows.cmd').read_text()
assert '-p:SmartCleanTargetFramework=%SDK_TARGET%' in cmd
assert 'dotnet restore' in cmd and '--configfile "NuGet.Config"' in cmd
assert 'dotnet publish' in cmd and '--no-restore' in cmd
assert 'output\\logs\\restore.log' in cmd
assert (project/'Diagnose-Windows.cmd').exists()
assert 'dotnet --version' in cmd and 'SDK_MAJOR' in cmd
assert 'dotnet nuget list source' in cmd
assert (project/'tests/SmartClean.Core.Tests/Program.cs').exists()
print('PASS: XAML/XML, event wiring, read-only safeguards, publish configuration, NuGet source, selected RID, diagnostic/build assets, startup stages')
assert (project/'.github/workflows/release.yml').exists()
assert (project/'installer/SmartClean.iss').exists()
updater = (project/'src/SmartClean.Core/Updates/ReleaseClient.cs').read_text()
assert 'Sha256' in updater and 'FixedTimeEquals' in updater and '3AYZE/SmartClean' in updater
assert 'SmartClean-Setup.exe' in (project/'.github/workflows/release.yml').read_text()
print('NOT RUN: Windows build or .NET runtime unit tests (Windows .NET SDK unavailable here)')
