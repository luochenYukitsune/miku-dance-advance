$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try{
 $indexPath=Join-Path $projectRoot 'src/SOURCE_INDEX.json'
 $index=Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8|ConvertFrom-Json
 # Discover every source project so new product/test/tool files cannot silently fall outside the snapshot.
 $paths=@(& rg --files src tests tools examples|Where-Object{[IO.Path]::GetExtension($_) -in '.cs','.csproj'}|ForEach-Object{$_.Replace('\','/')}|Sort-Object -Unique)
 $entries=@();$known=@{};foreach($entry in $index.files){$known[$entry.path]=$entry}
 foreach($relative in $paths){
  if($known.ContainsKey($relative)){$entry=$known[$relative]}
  else{
   $project=$index.projects|Where-Object{$relative.StartsWith($_.directory+'/')}|Select-Object -First 1
   if(-not $project){throw "Source ownership missing: $relative"}
   $entry=[pscustomobject]@{path=$relative;project=$project.name;role=$project.role;productModule=$project.productModule;relatedProductModules=$project.relatedProductModules;kind=if($relative.EndsWith('.csproj')){'project'}else{'csharp'};bytes=0;sha256='';verificationStatus='evidence-is-scoped-see-current-stage'}
  }
  $entry.bytes=(Get-Item -LiteralPath $relative).Length
  $entry.sha256=(Get-FileHash -LiteralPath $relative -Algorithm SHA256).Hash.ToLowerInvariant()
  $entries+=$entry
 }
 $index.files=$entries
 $supportByPath=@{};foreach($entry in $index.supportingFiles){$supportByPath[$entry.path]=$entry}
 $guiSupport=@(& rg --files src/Mmd.Modern.App -g '*.axaml')+@(& rg --files src/Mmd.Modern.App/Assets/Branding -g '*.png' -g '*.ico')+@(& rg --files src/Mmd.Modern.App/Assets/Fonts)+@(& rg --files src/Mmd.Modern.App/Assets/Localization -g '*.json' -g 'README.md')+@(
  'src/Mmd.Modern.App/LICENSE.Pfim.txt',
  'scripts/localize-editor-static.py','scripts/update-source-index.ps1',
  'scripts/package-editor.ps1','scripts/package-source.ps1','scripts/stage-editor-crt.ps1','installer/MDA.iss'
 )
 $guiVerification=@(& rg --files scripts/probes)+@(& rg --files scripts -g 'verify*.ps1')
 foreach($roleAndPaths in @(
  [pscustomobject]@{role='product-support';paths=$guiSupport},
  [pscustomobject]@{role='verification-only';paths=$guiVerification}
 )){
  foreach($relative in $roleAndPaths.paths){
   $relative=$relative.Replace('\','/')
   if(-not $supportByPath.ContainsKey($relative)){
    $supportByPath[$relative]=[pscustomobject]@{path=$relative;project='Mmd.Modern.App';role=$roleAndPaths.role;productModule='GUI';bytes=0;sha256=''}
   }
  }
 }
 $formatsModule=($index.projects|Where-Object name -eq 'Mmd.Modern.Formats').productModule
 foreach($relative in @('src/Mmd.Modern.Formats/Resources/cp932-duplicates.bin','src/Mmd.Modern.Formats/Resources/README.md','src/Mmd.Modern.Formats/LICENSE.unicode.txt')){
  if(-not $supportByPath.ContainsKey($relative)){$supportByPath[$relative]=[pscustomobject]@{path=$relative;project='Mmd.Modern.Formats';role='product-support';productModule=$formatsModule;bytes=0;sha256=''}}
 }
 $traditionalLicense='src/Mmd.Modern.Rendering.D3D9.Compatibility/LICENSE.MikuDanceStudio.txt'
 if(-not $supportByPath.ContainsKey($traditionalLicense)){
  $traditionalProject=$index.projects|Where-Object name -eq 'Mmd.Modern.Rendering.D3D9.Compatibility'
  $supportByPath[$traditionalLicense]=[pscustomobject]@{path=$traditionalLicense;project=$traditionalProject.name;role='product-support';productModule=$traditionalProject.productModule;bytes=0;sha256=''}
 }
 $cameraFixture='tests/Mmd.Modern.Application.Tests/Fixtures/original-camera-main.json'
 if(-not $supportByPath.ContainsKey($cameraFixture)){$supportByPath[$cameraFixture]=[pscustomobject]@{path=$cameraFixture;project='Mmd.Modern.Application.Tests';role='verification-only';productModule=$formatsModule;bytes=0;sha256=''}}
 $index.supportingFiles=@($supportByPath.Values|Sort-Object path)
 foreach($entry in $index.supportingFiles){$entry.bytes=(Get-Item -LiteralPath $entry.path).Length;$entry.sha256=(Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant()}
 foreach($project in $index.projects){
  $project.sourceAndProjectFileCount=@($entries|Where-Object project -eq $project.name).Count
  [xml]$projectXml=Get-Content -LiteralPath $project.path -Raw -Encoding UTF8
  $project.projectReferences=@($projectXml.SelectNodes('//ProjectReference')|ForEach-Object{
   $absolute=[IO.Path]::GetFullPath((Join-Path (Join-Path $projectRoot $project.directory) $_.Include))
   $absolute.Substring($projectRoot.Length+1).Replace('\','/')
  })
 }
 $index.generatedAtUtc=[DateTime]::UtcNow.ToString('o')
 $index.coverage.expectedCsharpAndProjectFiles=$paths.Count;$index.coverage.indexedCsharpAndProjectFiles=$entries.Count
 $index.coverage.srcCsharpAndProjectFiles=@($paths|Where-Object{$_.StartsWith('src/')}).Count
 $index.coverage.testCsharpAndProjectFiles=@($paths|Where-Object{$_.StartsWith('tests/')}).Count
 $index.coverage.sourceProjectCount=@($index.projects|Where-Object{$_.directory.StartsWith('src/')}).Count
 $index.coverage.testProjectCount=@($index.projects|Where-Object role -eq 'test').Count
 $index.coverage.toolProjectCount=@($index.projects|Where-Object role -eq 'tool').Count
 $index.coverage.supportingFileCount=$index.supportingFiles.Count
 $index|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $indexPath -Encoding UTF8
 [pscustomobject]@{files=$entries.Count;src=$index.coverage.srcCsharpAndProjectFiles;tests=$index.coverage.testCsharpAndProjectFiles;support=$index.supportingFiles.Count}
}finally{Pop-Location}
