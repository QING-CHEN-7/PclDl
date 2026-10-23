using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PclDl.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class TestAttribute : Attribute { }

    internal sealed class AssertException : Exception
    {
        public AssertException(string message) : base(message) { }
    }

    internal static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new AssertException(message);
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertException(message + "（期望 " + expected + "，实际 " + actual + "）");
        }

        public static void NotNull(object value, string message)
        {
            if (value == null) throw new AssertException(message);
        }
    }

    internal static class Program
    {
        private static int Main()
        {
            var tests = Assembly.GetExecutingAssembly()
                .GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.Name)
                .ToList();

            int passed = 0, failed = 0;
            foreach (var test in tests)
            {
                var name = test.DeclaringType.Name + "." + test.Name;
                try
                {
                    object instance = test.IsStatic ? null : Activator.CreateInstance(test.DeclaringType);
                    test.Invoke(instance, null);
                    passed++;
                    Console.WriteLine("  PASS  " + name);
                }
                catch (Exception ex)
                {
                    failed++;
                    var inner = (ex is TargetInvocationException && ex.InnerException != null) ? ex.InnerException : ex;
                    Console.WriteLine("  FAIL  " + name + " :: " + inner.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + "，失败 " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
