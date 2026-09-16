using System.Reflection;
using System.Runtime.InteropServices;

// 版本信息（写进 exe 属性，杀软和资源管理器都会读）
[assembly: AssemblyTitle("DeepSeek 余额挂件")]
[assembly: AssemblyDescription("桌面常驻挂件：显示 DeepSeek API 账户余额，点击刷新")]
[assembly: AssemblyProduct("DeepSeekBalanceWidget")]
[assembly: AssemblyCompany("个人自用工具")]
[assembly: AssemblyCopyright("Copyright (C) 2026")]
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]
[assembly: AssemblyInformationalVersion("1.6.0")]

// 说明：本程序不修改系统设置、不写注册表、不开监听端口、不加载驱动，
// 只做三件事：读 config.json、访问 api.deepseek.com 查余额、在当前目录写日志。
[assembly: ComVisible(false)]
