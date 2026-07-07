# Reflection dump of Nuclear Option's Assembly-CSharp.dll (run under Windows PowerShell 5.1 / .NET Framework)
param(
    [string]$Managed = "D:\SteamLibrary\steamapps\common\Nuclear Option\NuclearOption_Data\Managed",
    [string]$OutDir  = "$PSScriptRoot\apidump"
)
$ErrorActionPreference = "Continue"
New-Item -ItemType Directory -Force $OutDir | Out-Null

# Resolve dependencies from the Managed folder
$resolver = [System.ResolveEventHandler]{
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    $p = Join-Path $Managed "$name.dll"
    if (Test-Path $p) { return [System.Reflection.Assembly]::LoadFrom($p) }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $Managed "Assembly-CSharp.dll"))
try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }

# 1) Full type list: Name : BaseType
$types | Sort-Object FullName | ForEach-Object {
    "{0} : {1}" -f $_.FullName, $(if ($_.BaseType) { $_.BaseType.FullName } else { "" })
} | Set-Content (Join-Path $OutDir "types.txt")

# 2) Detailed member dump for interesting types
$keywords = @('Aircraft','Plane','Unit','Vehicle','Missile','Weapon','Hardpoint','Airbase','AirBase','Base','Runway',
    'Faction','HQ','Team','Radar','Sensor','Track','Datalink','Detect','Target','Player','Gear','Undercarriage',
    'Fuel','Damage','Health','Kill','Death','Destroy','Mission','Game','Network','Pause','Menu','Settings','Unit',
    'RWR','Threat','Warning','Countermeasure','Flare','Chaff','Land','Takeoff','Spawn','Loadout','Pilot','Cockpit','Map','Objective')

$interesting = $types | Where-Object {
    $t = $_
    -not $t.IsNested -and ($keywords | Where-Object { $t.Name -match $_ }).Count -gt 0
} | Sort-Object FullName

$bf = [System.Reflection.BindingFlags]"Public,NonPublic,Instance,Static,DeclaredOnly"
$sb = New-Object System.Text.StringBuilder
foreach ($t in $interesting) {
    [void]$sb.AppendLine(("=" * 100))
    $kind = if ($t.IsEnum) { "enum" } elseif ($t.IsInterface) { "interface" } elseif ($t.IsValueType) { "struct" } else { "class" }
    [void]$sb.AppendLine("$kind $($t.FullName) : $($t.BaseType.FullName)")
    if ($t.IsEnum) {
        try { [void]$sb.AppendLine("  values: " + ([System.Enum]::GetNames($t) -join ", ")) } catch {}
        continue
    }
    try {
        foreach ($f in $t.GetFields($bf)) {
            $vis = if ($f.IsPublic) { "public" } else { "private" }
            $st  = if ($f.IsStatic) { " static" } else { "" }
            [void]$sb.AppendLine("  F $vis$st $($f.FieldType.Name) $($f.Name)")
        }
        foreach ($p in $t.GetProperties($bf)) {
            [void]$sb.AppendLine("  P $($p.PropertyType.Name) $($p.Name) { $(if($p.CanRead){'get;'})$(if($p.CanWrite){'set;'}) }")
        }
        foreach ($m in ($t.GetMethods($bf) | Where-Object { -not $_.IsSpecialName })) {
            try {
                $vis = if ($m.IsPublic) { "public" } else { "private" }
                $st  = if ($m.IsStatic) { " static" } else { "" }
                $ps = ($m.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
                [void]$sb.AppendLine("  M $vis$st $($m.ReturnType.Name) $($m.Name)($ps)")
            } catch {
                [void]$sb.AppendLine("  M <unreadable> $($m.Name)")
            }
        }
    } catch {
        [void]$sb.AppendLine("  <member enumeration failed: $($_.Exception.Message)>")
    }
}
$sb.ToString() | Set-Content (Join-Path $OutDir "members.txt")

"Types: $($types.Count), interesting: $($interesting.Count)"
"Output: $OutDir"
