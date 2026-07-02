using System;
using System.IO;
using System.Linq;
using System.Reflection;

class InspectThermoDlls
{
    static void Main(string[] args)
    {
        string folder = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        string[] keywords = new[]
        {
            "deconv",
            "decon",
            "xtract",
            "respect",
            "isotope",
            "charge",
            "centroid",
            "massprecision",
            "averagine",
            "spectrum"
        };

        var dlls = Directory.GetFiles(folder, "ThermoFisher*.dll")
            .Concat(Directory.GetFiles(folder, "*.dll").Where(f => Path.GetFileName(f).IndexOf("Thermo", StringComparison.OrdinalIgnoreCase) >= 0))
            .Distinct()
            .OrderBy(f => f)
            .ToList();

        Console.WriteLine("DLLs found:");
        foreach (var dll in dlls)
        {
            Console.WriteLine("  " + dll);
        }

        Console.WriteLine();

        foreach (var dll in dlls)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine(Path.GetFileName(dll));
            Console.WriteLine("================================================================================");

            Assembly asm;

            try
            {
                asm = Assembly.LoadFrom(dll);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not load: " + ex.Message);
                continue;
            }

            Type[] types;

            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }

            foreach (var type in types.OrderBy(t => t.FullName))
            {
                bool typeMatches = keywords.Any(k => type.FullName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Where(m => keywords.Any(k => m.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(m => m.Name)
                    .ToList();

                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Where(p => keywords.Any(k => p.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(p => p.Name)
                    .ToList();

                if (!typeMatches && methods.Count == 0 && properties.Count == 0)
                {
                    continue;
                }

                Console.WriteLine();
                Console.WriteLine("TYPE: " + type.FullName);

                foreach (var property in properties)
                {
                    Console.WriteLine("  PROPERTY: " + property.PropertyType.FullName + " " + property.Name);
                }

                foreach (var method in methods)
                {
                    Console.WriteLine("  METHOD: " + MethodSignature(method));
                }
            }

            Console.WriteLine();
        }
    }

    static string MethodSignature(MethodInfo method)
    {
        string parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
        return method.ReturnType.Name + " " + method.Name + "(" + parameters + ")";
    }
}