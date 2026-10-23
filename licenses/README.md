# Bundled upstream notices

These are upstream license texts for dependencies whose NuGet packages omit the
full text. `build.py` includes these in the generated third-party notices along
with OpenAxis's license and BouncyCastle's packaged license.

- `MessagePack.txt`: MessagePack-CSharp commit
  `7fd78bbf3476e03beae8b0f7a521125525a8a64a`, the source revision recorded in
  MessagePack 2.5.303's NuGet metadata (license text unchanged):
  https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/7fd78bbf3476e03beae8b0f7a521125525a8a64a/LICENSE
- `Microsoft.NET.StringTools.txt`: MSBuild commit
  `07e2947214f1a9f28a3517762c939d5bebb5f525`, recorded in StringTools 17.6.3:
  https://github.com/dotnet/msbuild/blob/07e2947214f1a9f28a3517762c939d5bebb5f525/LICENSE
- `Apache-2.0.txt`: https://www.apache.org/licenses/LICENSE-2.0.txt

Revisit these texts when upgrading the corresponding dependencies.
