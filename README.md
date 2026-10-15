# AssetStudio（Unity 6 / Web / macOS fork）

這個分支在原本的 AssetStudio 上加入：

- **Unity 6000.x 支援**（以 6000.3.24f1 的 bundle 實測；6000.4 / 6000.5 的欄位變化依 Unity 各版本 TypeTree 資料庫一併處理）
- **AssetStudioCLI**：跨平台命令列工具（macOS / Linux / Windows），可以列出、匯出、解包 bundle
- **AssetStudioWeb**：純靜態網頁版。檔案在瀏覽器本機解析（.NET WebAssembly + WebAssembly 版貼圖解碼器），不會上傳到任何伺服器

原本的 Windows GUI（AssetStudioGUI）照舊可以建置使用，也一起受惠於上述的解析修正。

## 網頁版（AssetStudioWeb）

### 使用

把 `.assetbundle` / `.assets` / 整個資料夾拖進頁面即可。支援：

- 資產列表（類型篩選、搜尋、排序、Shift/Ctrl 多選）、Texture2D / Sprite 預覽、文字、Shader、字型預覽、TypeTree Dump（JSON）
- 匯出成 ZIP，或在 Chrome / Edge 直接寫入資料夾；模式：轉換（PNG/WebP/JPEG、文字、字型、JSON…）、原始資料、Dump
- 「Bundle 內容」分頁：解出 bundle 內的原始檔（serialized file、.resS、.resource）
- 網址載入：`index.html?url=<bundle 網址>`。跨網域檔案需要該伺服器回傳 CORS header，否則請用下面的 `serve.mjs`（內建 proxy），或先下載再拖進來

### 建置與部署

```sh
cd AssetStudioWeb
dotnet publish -c Release -o publish      # 需要 .NET 9 SDK，不需要另外安裝 workload
node serve.mjs                            # http://localhost:8080 （含 /proxy?url= 轉送，只聽 localhost）
```

`publish/wwwroot` 整個資料夾就是靜態網站，可以直接放到任何靜態主機（S3/CloudFront、nginx、GitHub Pages…）。
注意事項：

- 必須用 http(s) 開啟，不能直接雙擊 `index.html`（file:// 無法載入 WebAssembly 模組）
- `.wasm` 請以 `application/wasm` 提供；`_framework` 內有預先壓縮的 `.br`/`.gz`，主機若支援可直接使用
- 若網頁和 bundle 放在同一個網域（例如同一個 CloudFront），就能直接用 `?url=` 載入而不需要 CORS

### 架構

| 部分 | 說明 |
| --- | --- |
| `AssetStudioWeb/WebApi.cs` | 以 `[JSExport]` 暴露 AssetStudio 核心給 JavaScript |
| `wwwroot/worker.js` | 在 Web Worker 中執行 .NET runtime，UI 不會卡住；Sprite 裁切、PNG 編碼、ZIP 打包也在這裡 |
| `wwwroot/native/assetstudio-native.wasm` | `Texture2DDecoderNative`（DXT/BC、ETC/EAC、ASTC、PVRTC、ATC、Crunch）＋ LZ4 / LZMA 解壓縮，用 Emscripten 編譯 |
| `wwwroot/app.js` | 介面 |

`assetstudio-native.wasm` 已經編譯好放在 repo 內，只有修改 C/C++ 原始碼時才需要重新編譯：
安裝 [Emscripten](https://emscripten.org/docs/getting_started/downloads.html) 後執行 `Texture2DDecoderNative/build-wasm.sh`。

效能（範例 22 MB LZMA bundle，15,725 個物件，Apple Silicon + Chrome）：載入約 1 秒、匯出全部 4,634 個資產約 19 秒；
匯出結果與 CLI 逐檔比對完全一致（PNG 逐像素相同）。

## 命令列版（AssetStudioCLI）

```sh
dotnet build AssetStudioCLI -c Release     # macOS/Linux 會自動用 clang 編譯 libTexture2DDecoderNative
AssetStudioCLI=AssetStudioCLI/bin/Release/net9.0/AssetStudioCLI

$AssetStudioCLI info   game.assetbundle                       # Unity 版本、平台、物件數
$AssetStudioCLI list   game.assetbundle -t Texture2D,Sprite   # 列出資產
$AssetStudioCLI export game.assetbundle -o out                # 匯出全部可匯出的資產
$AssetStudioCLI export bundles/ -o out -t Sprite -g type      # 整個資料夾、只匯出 Sprite、依類型分資料夾
$AssetStudioCLI unpack game.assetbundle -o out                # 解出 bundle 內的原始檔
$AssetStudioCLI verify game.assetbundle                       # 用檔案內的 TypeTree 檢查各類別的解析是否正確
```

選項：`-m convert|raw|dump`、`-g container|type|source|none`、`-f <關鍵字>`、`--image-format png|jpeg|bmp|tga`、`--all`、`--unity-version <版本>`。

macOS 單一執行檔：

```sh
dotnet publish AssetStudioCLI -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o dist/mac
# dist/mac/AssetStudioCLI 與 libTexture2DDecoderNative.dylib 放在一起即可使用
```

`verify` 會把 AssetStudio 手寫的類別解析結果和 bundle 內的 TypeTree 逐欄位比對；遇到新的 Unity 版本時，先跑它就能知道哪些類別需要調整。

## Unity 6 相關修正

- Texture / Texture2D：2023.2 移除 `m_ForcedFallbackFormat`、`m_DownscaleFallback`；2022.2 新增 `m_MipmapLimitGroupName`
- Renderer：2023.2 RayTracing AccelStruct 欄位、6000.0 `m_SmallMeshCulling`、6000.2 Mesh LOD 欄位
- Shader：2021.3.10+ `m_PlayerSubPrograms` / `m_ParameterBlobIndices`、`stageCounts`；2023.1 移除 pass 的 `m_EditorDataHash` / `m_Platforms`；匯出 Shader 時支援 player sub programs
- AnimatorController：6000.2 `m_EntityIdValues`、6000.4 `m_EvaluateTransitionsOnStart`；AnimationClip：2022.3.19+ StreamedClip `curveCount` 拆成兩個 UInt16
- Sprite：6000.5 欄位調整
- TypeTree：`char` 改為 1 byte（修正含 `vector<char>` 的 Font dump）、補上 Unity 6 新增的 common strings（`EntityId` 等），未知的葉節點依大小讀取
- Sprite Tight 裁切改用自己的三角形光柵化，修正原本 ImageSharp 多邊形填充在相鄰三角形邊界留下的透明細縫

## 已知限制

- AudioClip 在 Unity 5 之後是 FMOD FSB5（多為 Vorbis）。FMOD 只有 Windows 版，所以 macOS / 網頁版匯出的是原始 `.fsb`，可用 [vgmstream](https://vgmstream.org/) 轉檔
- FBX 匯出（Animator/模型）仍只有 Windows GUI 支援
- 網頁版受瀏覽器 WebAssembly 記憶體限制（檔案在記憶體中會有原始與解壓縮兩份），建議單次載入不超過數百 MB
- 6000.6（目前仍是 alpha）的 Shader 參數結構與 SpriteAtlas 變更尚未處理；遇到新版本可先用 `AssetStudioCLI verify` 檢查

---

# AssetStudio
[![Build status](https://ci.appveyor.com/api/projects/status/rnu7l90422pdewx4?svg=true)](https://ci.appveyor.com/project/Perfare/assetstudio/branch/master/artifacts)

**None of the repo, the tool, nor the repo owner is affiliated with, or sponsored or authorized by, Unity Technologies or its affiliates.**

AssetStudio is a tool for exploring, extracting and exporting assets and assetbundles.

## Features
* Support version:
  * 3.4 - 6000.x
* Support asset types:
  * **Texture2D** : convert to png, tga, jpeg, bmp
  * **Sprite** : crop Texture2D to png, tga, jpeg, bmp
  * **AudioClip** : mp3, ogg, wav, m4a, fsb. support convert FSB file to WAV(PCM)
  * **Font** : ttf, otf
  * **Mesh** : obj
  * **TextAsset**
  * **Shader**
  * **MovieTexture**
  * **VideoClip**
  * **MonoBehaviour** : json
  * **Animator** : export to FBX file with bound AnimationClip

## Requirements

- AssetStudio.net472
   - [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472)
- AssetStudio.net5
   - [.NET Desktop Runtime 5.0](https://dotnet.microsoft.com/download/dotnet/5.0)
- AssetStudio.net6
   - [.NET Desktop Runtime 6.0](https://dotnet.microsoft.com/download/dotnet/6.0)


## Usage

### Load Assets/AssetBundles

Use **File-Load file** or **File-Load folder**.

When AssetStudio loads AssetBundles, it decompresses and reads it directly in memory, which may cause a large amount of memory to be used. You can use **File-Extract file** or **File-Extract folder** to extract AssetBundles to another folder, and then read.

### Extract/Decompress AssetBundles

Use **File-Extract file** or **File-Extract folder**.

### Export Assets

use **Export** menu.

### Export Model

Export model from "Scene Hierarchy" using the **Model** menu.

Export Animator from "Asset List" using the **Export** menu.

#### With AnimationClip

Select model from "Scene Hierarchy" then select the AnimationClip from "Asset List", using **Model-Export selected objects with AnimationClip** to export.

Export Animator will export bound AnimationClip or use **Ctrl** to select Animator and AnimationClip from "Asset List", using **Export-Export Animator with selected AnimationClip** to export.

### Export MonoBehaviour

When you select an asset of the MonoBehaviour type for the first time, AssetStudio will ask you the directory where the assembly is located, please select the directory where the assembly is located, such as the `Managed` folder.

#### For Il2Cpp

First, use my another program [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) to generate dummy dll, then when using AssetStudio to select the assembly directory, select the dummy dll folder.

## Build

* Visual Studio 2022 or newer
* **AssetStudioFBXNative** uses [FBX SDK 2020.2.1](https://www.autodesk.com/developer-network/platform-technologies/fbx-sdk-2020-2-1), before building, you need to install the FBX SDK and modify the project file, change include directory and library directory to point to the FBX SDK directory

## Open source libraries used

### Texture2DDecoder
* [Ishotihadus/mikunyan](https://github.com/Ishotihadus/mikunyan)
* [BinomialLLC/crunch](https://github.com/BinomialLLC/crunch)
* [Unity-Technologies/crunch](https://github.com/Unity-Technologies/crunch/tree/unity)
