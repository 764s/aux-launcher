# WinDivert

本项目使用未经修改的 WinDivert 2.2.2-A x64 动态库和已签名驱动，仅用于底层网络捕获与重注入。应用层代码为本项目实现。

- 官方发行页：https://github.com/basil00/WinDivert/releases/tag/v2.2.2
- 原始包：https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip
- 对应源码：https://github.com/basil00/WinDivert/tree/v2.2.2
- 官方文档：https://reqrypt.org/windivert-doc.html
- 许可：随附原始 `LICENSE`（本集成选择 LGPLv3，未静态链接或修改该库）。发布目录保留可替换的独立 DLL、SYS 和许可文本。

校验值（SHA-256）：

| 文件 | SHA-256 |
| --- | --- |
| 原始 ZIP | `63cb41763bb4b20f600b6de04e991a9c2be73279e317d4d82f237b150c5f3f15` |
| WinDivert.dll | `c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2` |
| WinDivert64.sys | `8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2` |

交付时 Windows Authenticode 检查结果为 `Valid`，实际驱动签名主体为“成都密思听科技有限公司”。本项目未重新编译、签名或改动驱动。不同机器的系统策略仍可能阻止其加载，此时启动器明确失败，不放行目标。
