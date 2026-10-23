using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace PclDl.Probe
{
    /// <summary>
    /// 只读能力探测：加载目标 PCL 程序集，检查插件依赖的锚点是否存在。
    /// 不修改任何文件，不启动目标进程。
    /// </summary>
    internal static class Program
    {
        private static int _failed;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0)
            {
                Console.WriteLine("用法：PclDl.Probe <PCL 可执行文件路径>");
                return 2;
            }

            var exePath = Path.GetFullPath(args[0]);
            if (!File.Exists(exePath))
            {
                Console.Error.WriteLine("找不到可执行文件：" + exePath);
                return 2;
            }

            Console.WriteLine("目标：" + exePath);
            Console.WriteLine("大小：" + new FileInfo(exePath).Length + " 字节");
            Console.WriteLine();

            Assembly asm;
            try { asm = Assembly.LoadFrom(exePath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("加载程序集失败：" + ex.Message);
                return 2;
            }

            Console.WriteLine("程序集：" + asm.GetName().Name + " " + asm.GetName().Version);
            Console.WriteLine();

            // 宿主骨架
            var formMain = CheckType(asm, "PCL.FormMain", true);
            CheckField(formMain, "PanMainRight", true, "右侧内容容器");
            CheckField(formMain, "PanMainLeft", false, "左侧导航容器");
            CheckField(formMain, "PanLeft", false, "导航区");

            // 页面基类
            var pageRight = CheckType(asm, "PCL.MyPageRight", true);
            CheckDerivedFrom(pageRight, typeof(System.Windows.Controls.Decorator), true);
            CheckParameterlessCtor(pageRight, true);
            var pageLeft = CheckType(asm, "PCL.MyPageLeft", false);

            // 下载页锚点（仅在存在时报告，不作为必要依赖）
            var install = CheckType(asm, "PCL.PageDownloadInstall", false);
            if (install != null)
            {
                CheckField(install, "PanLoad", false);
                CheckField(install, "PanAllBack", false);
                var loaderInit = install.GetMethod("LoaderInit", BindingFlags.Public | BindingFlags.Instance);
                Report("PageDownloadInstall.LoaderInit", loaderInit != null,
                    loaderInit == null ? "缺失" : (loaderInit.IsVirtual ? "public virtual" : "public 非虚"));
            }

            var modDownload = CheckType(asm, "PCL.ModDownload", false);
            if (modDownload != null)
            {
                CheckField(modDownload, "DlClientListLoader", false);
            }

            Console.WriteLine();
            Console.WriteLine(_failed == 0 ? "结论：锚点齐全，可挂载（D1 入口被裁或 D0 完整）" : ("结论：有 " + _failed + " 项关键锚点缺失，插件将降级"));
            return _failed == 0 ? 0 : 1;
        }

        private static Type CheckType(Assembly asm, string name, bool required)
        {
            var t = asm.GetType(name, false);
            Report("类型 " + name, t != null, t == null ? "缺失" : "存在");
            if (t == null && required) _failed++;
            return t;
        }

        private static void CheckField(Type owner, string name, bool required, string note = null)
        {
            if (owner == null) { if (required) _failed++; return; }

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var f = owner.GetField(name, flags);
            if (f != null)
            {
                Report("成员 " + owner.Name + "." + name, true, "字段 " + f.FieldType.Name + (note == null ? "" : "  (" + note + ")"));
                return;
            }

            var p = owner.GetProperty(name, flags);
            if (p != null)
            {
                Report("成员 " + owner.Name + "." + name, true, "属性 " + p.PropertyType.Name + (note == null ? "" : "  (" + note + ")"));
                return;
            }

            // VB WithEvents 会生成 _Name 私有字段
            var backing = owner.GetField("_" + name, flags);
            if (backing != null)
            {
                Report("成员 " + owner.Name + "." + name, true, "字段 _" + name + " : " + backing.FieldType.Name + (note == null ? "" : "  (" + note + ")"));
                return;
            }

            Report("成员 " + owner.Name + "." + name, false, "缺失");
            if (required) _failed++;
        }

        private static void CheckDerivedFrom(Type t, Type baseType, bool required)
        {
            if (t == null) { if (required) _failed++; return; }
            var ok = baseType.IsAssignableFrom(t);
            Report("继承 " + t.Name + " : " + baseType.Name, ok, ok ? "满足" : ("实际基类 " + (t.BaseType == null ? "null" : t.BaseType.FullName)));
            if (!ok && required) _failed++;
        }

        private static void CheckParameterlessCtor(Type t, bool required)
        {
            if (t == null) { if (required) _failed++; return; }
            var ok = t.GetConstructor(Type.EmptyTypes) != null;
            Report("构造 " + t.Name + "()", ok, ok ? "可反射实例化" : "无公共无参构造");
            if (!ok && required) _failed++;
        }

        private static void Report(string what, bool ok, string detail)
        {
            Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what.PadRight(44) + detail);
        }
    }
}
