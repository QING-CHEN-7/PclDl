using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace PclDl.Plugin.Host
{
    /// <summary>
    /// 唯一的反射出口。所有对 PCL 的依赖都在这里以"别名"形式声明，
    /// 版本漂移时只需调整本文件。
    /// </summary>
    public sealed class PclBinder
    {
        public Assembly PclAssembly { get; private set; }
        public Type FormMain { get; private set; }
        public Type PageRight { get; private set; }
        public Type PageLeft { get; private set; }

        public MemberHandle PanMainRight { get; private set; }
        public MemberHandle PanMainLeft { get; private set; }
        public MemberHandle PanLeft { get; private set; }
        public MemberHandle PageCurrent { get; private set; }
        /// <summary>当前次级页面（PageSubType）：Download 主页面下 0=安装，2..6=社区资源各分类。</summary>
        public MemberHandle PageCurrentSub { get; private set; }

        public readonly List<string> Missing = new List<string>();

        public bool IsBound { get { return Missing.Count == 0 && FormMain != null && PageRight != null; } }

        /// <summary>人类可读的绑定状态，用于自检面板与日志。</summary>
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("PCL 程序集：" + (PclAssembly == null ? "-" : PclAssembly.GetName().Name + " " + PclAssembly.GetName().Version));
            sb.AppendLine("FormMain：" + (FormMain != null ? "ok" : "缺失"));
            sb.AppendLine("PanMainRight：" + (PanMainRight != null ? "ok" : "缺失"));
            sb.AppendLine("MyPageRight：" + (PageRight != null ? "ok" : "缺失"));
            sb.AppendLine("MyPageLeft：" + (PageLeft != null ? "ok" : "缺失"));
            sb.AppendLine("PageCurrent：" + (PageCurrent != null ? "ok" : "缺失（将退化为启动即挂载）"));
            if (Missing.Count > 0) sb.AppendLine("缺失项：" + string.Join(",", Missing));
            return sb.ToString().TrimEnd();
        }

        public static PclBinder TryBind(Assembly pclAssembly)
        {
            var binder = new PclBinder { PclAssembly = pclAssembly };
            binder.FormMain = binder.RequireType("PCL.FormMain");
            binder.PageRight = binder.RequireType("PCL.MyPageRight");
            binder.PageLeft = binder.OptionalType("PCL.MyPageLeft");

            if (binder.FormMain != null)
            {
                binder.PanMainRight = binder.RequireMember(binder.FormMain, "PanMainRight");
                binder.PanMainLeft = binder.OptionalMember(binder.FormMain, "PanMainLeft");
                binder.PanLeft = binder.OptionalMember(binder.FormMain, "PanLeft");
                binder.PageCurrent = binder.OptionalMember(binder.FormMain, "PageCurrent");
                binder.PageCurrentSub = binder.OptionalMember(binder.FormMain, "PageCurrentSub");
            }
            return binder;
        }

        private Type RequireType(string name)
        {
            var t = PclAssembly.GetType(name, false);
            if (t == null) Missing.Add("type:" + name);
            return t;
        }

        private Type OptionalType(string name)
        {
            return PclAssembly.GetType(name, false);
        }

        private MemberHandle RequireMember(Type owner, string name)
        {
            var handle = MemberHandle.Resolve(owner, name);
            if (handle == null) Missing.Add("member:" + owner.Name + "." + name);
            return handle;
        }

        private MemberHandle OptionalMember(Type owner, string name)
        {
            return MemberHandle.Resolve(owner, name);
        }

        /// <summary>反射构造 MyPageRight 外壳（AdornerDecorator），并把内容挂到 Child。</summary>
        public FrameworkElement CreateShell(FrameworkElement content, string shellName = null)
        {
            if (PageRight == null) throw new InvalidOperationException("MyPageRight 未绑定");
            var shell = shellName != null ? PclAssembly.GetType(shellName, false) : PageRight;
            if (shell == null) shell = PageRight;

            var instance = (FrameworkElement)Activator.CreateInstance(shell);
            if (content != null)
            {
                // MyPageRight 继承 AdornerDecorator -> Decorator，Child 为公开属性
                var decorator = instance as Decorator;
                if (decorator != null) decorator.Child = content;
                else
                {
                    var prop = instance.GetType().GetProperty("Child");
                    if (prop != null) prop.SetValue(instance, content);
                }
            }
            return instance;
        }

        public static object GetMember(object target, MemberHandle handle)
        {
            return handle == null ? null : handle.Get(target);
        }

        public static void SetMember(object target, MemberHandle handle, object value)
        {
            if (handle != null) handle.Set(target, value);
        }
    }

    /// <summary>字段/属性/WithEvents 后备字段的统一访问句柄。</summary>
    public sealed class MemberHandle
    {
        public Type Owner;
        public string Name;
        public FieldInfo Field;
        public PropertyInfo Property;

        public static MemberHandle Resolve(Type owner, string name)
        {
            if (owner == null) return null;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            var field = owner.GetField(name, flags);
            if (field != null) return new MemberHandle { Owner = owner, Name = name, Field = field };

            var prop = owner.GetProperty(name, flags);
            if (prop != null) return new MemberHandle { Owner = owner, Name = name, Property = prop };

            // VB WithEvents 生成 _Name 私有字段
            var backing = owner.GetField("_" + name, flags);
            if (backing != null) return new MemberHandle { Owner = owner, Name = name, Field = backing };

            return null;
        }

        public object Get(object target)
        {
            if (Field != null) return Field.GetValue(target);
            if (Property != null) return Property.GetValue(target, null);
            return null;
        }

        public void Set(object target, object value)
        {
            if (Field != null) Field.SetValue(target, value);
            else if (Property != null && Property.CanWrite) Property.SetValue(target, value, null);
        }
    }
}
