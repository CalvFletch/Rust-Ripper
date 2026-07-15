# Golden regression set: re-export reference assets through the daemon and
# assert structural facts about the GLBs. Run after exporter changes.
# Requires: ripper serve on 17071 (bundles load on demand).
param(
    [string]$Daemon = "http://127.0.0.1:17071",
    [string]$OutDir = "$env:TEMP\rust_ripper_golden"
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$script:fail = 0

function Get-GlbJson([string]$path) {
    $fs = [IO.File]::OpenRead($path)
    try {
        $br = New-Object IO.BinaryReader($fs)
        $null = $br.ReadUInt32(); $null = $br.ReadUInt32(); $null = $br.ReadUInt32()  # magic, version, length
        $jsonLen = $br.ReadUInt32(); $null = $br.ReadUInt32()                          # chunk len, chunk type
        [Text.Encoding]::UTF8.GetString($br.ReadBytes($jsonLen)) | ConvertFrom-Json
    } finally { $fs.Close() }
}

function Assert([string]$name, $condition) {
    if ($condition) { Write-Host "PASS  $name" }
    else { Write-Host "FAIL  $name" -ForegroundColor Red; $script:fail++ }
}

function Export-Asset([string]$query) {
    $out = [uri]::EscapeDataString($OutDir)
    $resp = curl.exe -s --max-time 590 "$Daemon/export?q=$([uri]::EscapeDataString($query))&out=$out" | ConvertFrom-Json
    if (-not $resp.success) { throw "export failed: $query -> $($resp.message)" }
    $resp.path
}

# --- cockpit module: LOD filtering (component + quarantined name rule) ---
$g = Get-GlbJson (Export-Asset "1module_cockpit_with_engine")
Assert "cockpit exports"                     ($g.nodes.Count -gt 20)
Assert "cockpit: no LOD1/LOD2 nodes"         (-not ($g.nodes | Where-Object { $_.name -match "^LOD[12]" }))
Assert "cockpit: state variants hidden"      (($g.nodes | Where-Object { $_.extras.unity_hidden }).Count -ge 4)

# --- chassis: socket transforms force-kept ---
$g = Get-GlbJson (Export-Asset "2module_car_spawned.entity")
Assert "chassis: socket_mod_1 + socket_mod_2" ((($g.nodes | Where-Object { $_.name -like "socket_mod_*" }).Count) -eq 2)

# --- ceiling light: punctual lights + hidden utility geometry ---
$g = Get-GlbJson (Export-Asset "ceilinglight.deployed")
Assert "ceilinglight: KHR_lights_punctual"   ($g.extensionsUsed -contains "KHR_lights_punctual")
Assert "ceilinglight: origin flagged hidden" (($g.nodes | Where-Object { $_.name -eq "origin" -and $_.extras.unity_hidden }))

# --- searchlight: flare glow overlay from render state ---
$g = Get-GlbJson (Export-Asset "searchlight.deployed")
$flare = $g.materials | Where-Object { $_.name -eq "SearchlightFlare" }
Assert "searchlight: flare material BLEND"   ($flare -and $flare.alphaMode -eq "BLEND")

# --- wolf: fur profile (fuzz alpha composite), KHR specular, demoted colors ---
$g = Get-GlbJson (Export-Asset "wolf2")
Assert "wolf: fuzz composite image"          (($g.images | Where-Object { $_.name -eq "Wolf_Albedo_fuzzalpha" }))
$fur = $g.materials | Where-Object { $_.name -eq "WolfFur" }
Assert "wolf: fur material BLEND"            ($fur -and $fur.alphaMode -eq "BLEND")
Assert "wolf: animal-fur profile applied"    ($fur -and $fur.extras.rust_profile -eq "animal-fur")
Assert "wolf: KHR specular on body"          (($g.materials | Where-Object { $_.name -eq "Wolf" }).extensions.KHR_materials_specular)
Assert "wolf: _RUST_COLOR demotion"          (($g.meshes.primitives.attributes | Where-Object { $_._RUST_COLOR -ne $null }))

# --- container wall: runtime tint -> palette attributes from ColourLookup ---
$wallPath = Export-Asset "wall.container.full"
$g = Get-GlbJson $wallPath
$attrs = $g.meshes.primitives.attributes
Assert "wall: _RUST_CUSTOMCOLOUR_01 attribute" (($attrs | Where-Object { $_._RUST_CUSTOMCOLOUR_01 -ne $null }))
Assert "wall: _RUST_CUSTOMCOLOUR_16 attribute" (($attrs | Where-Object { $_._RUST_CUSTOMCOLOUR_16 -ne $null }))
Assert "wall: no invented bake (raw albedo)"   (-not ($g.images | Where-Object { $_.name -like "*_detailtint" }))
Assert "wall: blend mask sidecar"              (Test-Path (Join-Path (Split-Path $wallPath) "wall.container.full.shipping_container_mask.png"))
Assert "wall: tint map sidecar"                (Test-Path (Join-Path (Split-Path $wallPath) "wall.container.full.shipping_container_color_lookup.png"))

# --- barrel: detail paint baked + paint attribute ---
$g = Get-GlbJson (Export-Asset "loot-barrel-1")
Assert "barrel: detail tint baked image"     (($g.images | Where-Object { $_.name -like "*_detailtint" }))
Assert "barrel: _RUST_DETAILCOLOR attribute" (($g.meshes.primitives.attributes | Where-Object { $_._RUST_DETAILCOLOR -ne $null }))

Write-Host ""
if ($script:fail -eq 0) { Write-Host "GOLDEN SET: all checks passed" -ForegroundColor Green }
else { Write-Host "GOLDEN SET: $($script:fail) FAILURES" -ForegroundColor Red }
exit $script:fail
