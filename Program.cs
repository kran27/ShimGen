using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using File = System.IO.File;

var dllBytes = File.ReadAllBytes(args[0]);

// StringBuilder to create dll.h, a header containing the dll as a byte array
var dll_h = new StringBuilder();
Console.WriteLine("Creating dll.h");
dll_h.AppendLine("#pragma once");
dll_h.AppendLine();
dll_h.AppendLine($"unsigned char dllBytes[{dllBytes.Length}] = {{");
dll_h.Append("    ");
for (int i = 0; i < dllBytes.Length; i++)
{
    dll_h.Append($"0x{dllBytes[i]:X2}");
    if (i < dllBytes.Length - 1)
    {
        dll_h.Append(", ");
    }

    if (i % 16 == 15)
    {
        dll_h.AppendLine();
        dll_h.Append("    ");
    }
}
dll_h.Remove(dll_h.Length - 4, 4);
dll_h.AppendLine("};");

var peFile = new PeNet.PeFile(dllBytes);

// StringBuilder to create main.cpp, the main file of the shim
var main_cpp = new StringBuilder();
// StringBuilder to create the DllMain function
var DllMain = new StringBuilder();
// StringBuilder to create the typedefs
var typedefs = new StringBuilder();
// StringBuilder to create the function definitions
var funcs = new StringBuilder();

Console.WriteLine("Creating main.cpp");
main_cpp.AppendLine("#include \"dll.h\"");
main_cpp.AppendLine("#include \"MemoryModule/MemoryModule.h\"");
main_cpp.AppendLine();
main_cpp.AppendLine("HMEMORYMODULE hMModule = nullptr;");
main_cpp.AppendLine();

DllMain.AppendLine("BOOL APIENTRY DllMain( HMODULE hModule, DWORD  ul_reason_for_call, LPVOID lpReserved )");
DllMain.AppendLine("{");
DllMain.AppendLine("    switch (ul_reason_for_call)");
DllMain.AppendLine("    {");
DllMain.AppendLine("        case DLL_PROCESS_ATTACH:");
DllMain.AppendLine("            hMModule = MemoryLoadLibrary(dllBytes, sizeof(dllBytes));");
DllMain.AppendLine("            if (hMModule == nullptr)");
DllMain.AppendLine("            {");
DllMain.AppendLine("                return FALSE;");
DllMain.AppendLine("            }");
DllMain.AppendLine();

var dir = new DirectoryInfo("C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\VC\\Tools\\MSVC").GetDirectories();

funcs.AppendLine("extern \"C\" {");
Console.WriteLine("Creating function definitions");

var typedefsList = new List<string>();
var funcsList = new List<string>();
var DllMainList = new List<string>();

var completed = 0;

Parallel.For(0, peFile.ExportedFunctions.Length, i =>
{
    var export = peFile.ExportedFunctions[i];

    var undnamePath = dir[0] + "\\bin\\Hostx64\\x64\\undname.exe";
    var undnameProcess = new Process
    {
        StartInfo =
        {
            FileName = undnamePath,
            Arguments = $"\"{export.Name}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        }
    };
    undnameProcess.Start();
    undnameProcess.WaitForExit();
    var undname = undnameProcess.StandardOutput.ReadToEnd().Trim();

    var nameRegex = new Regex(@"is\s*:-\s*""(.*)""");
    var name = nameRegex.Match(undname).Groups[1].Value;

    var callingConventionRegex = new Regex(@"cdecl|stdcall|thiscall|fastcall");
    var callingConvention = "__" + callingConventionRegex.Match(name).Value;

    var returnTypeRegex = new Regex(@"^(?:(virtual|static|signed|unsigned)\s)?[^ ]+\s*\*?");
    var nameNoQual = name.Replace("public: ", "").Replace("protected: ", "").Replace("private: ", "");
    var returnType = returnTypeRegex.Match(nameNoQual).Value.Replace("virtual", "").Trim();
    returnType = returnType.Replace("static ", "");

    if (returnType == callingConvention)
        returnType = "void";
    else if (returnType == "class" || returnType == "struct")
        returnType = "void *";
    else if(returnType == "enum")
        returnType = "int";

    var argsRegex = new Regex(@"(?<=\().*(?=\))");
    var argsMatch = argsRegex.Match(name).Value;
    var argsCount = argsMatch.Count(c => c == ',');

    if (argsCount != 0 || argsRegex.Match(name).Value != "")
        argsCount++;

    var argSB = new StringBuilder();
    var argSB2 = new StringBuilder();
    for (var j = 0; j < argsCount; j++)
    {
        var argName = $"_{j}";
        argSB2.Append(argName);
        argSB.Append("int* __ptr64 " + argName);
        if (j < argsCount - 1)
        {
            argSB.Append(", ");
            argSB2.Append(", ");
        }
    }

    var typedefsChunk = new List<string>();
    var funcsChunk = new List<string>();
    var DllMainChunk = new List<string>();

    typedefsChunk.Add($"//{name}");
    if (callingConvention == "__")
    {
        typedefsChunk.Add($"using func{i}def = void *;");
        typedefsChunk.Add($"func{i}def func{i}og;");

        funcsChunk.Add($"#pragma comment(linker, \"/export:{export.Name}=func{i}\")");
        funcsChunk.Add($"    void * func{i};");
    }
    else
    {
        typedefsChunk.Add($"typedef {returnType}({callingConvention}* func{i}def)({argSB});");
        typedefsChunk.Add($"func{i}def func{i}og;");

        funcsChunk.Add($"#pragma comment(linker, \"/export:{export.Name}=func{i}\")");
        funcsChunk.Add($"    {returnType} {callingConvention} func{i}({argSB})");
        funcsChunk.Add("    {");
        funcsChunk.Add($"        return func{i}og({argSB2});");
        funcsChunk.Add("    }");
    }
    DllMainChunk.Add($"            func{i}og = reinterpret_cast<func{i}def> (MemoryGetProcAddress(hMModule, \"{export.Name}\"));");
    //Progress Bar
    lock (typedefsList)
    {
        typedefsList.AddRange(typedefsChunk);
    }

    lock (funcsList)
    {
        funcsList.AddRange(funcsChunk);
    }

    lock (DllMainList)
    {
        DllMainList.AddRange(DllMainChunk);
    }

    // Progress Bar
    Interlocked.Increment(ref completed);
    Console.Write($"\r{completed}/{peFile.ExportedFunctions.Length}");
});
Console.WriteLine();

foreach (var line in typedefsList)
{
    typedefs.AppendLine(line);
}
foreach (var line in funcsList)
{
    funcs.AppendLine(line);
}
foreach (var line in DllMainList)
{
    DllMain.AppendLine(line);
}

funcs.AppendLine("}");

DllMain.AppendLine();
DllMain.AppendLine("        case DLL_THREAD_ATTACH:");
DllMain.AppendLine("        case DLL_THREAD_DETACH:");
DllMain.AppendLine("        case DLL_PROCESS_DETACH:");
DllMain.AppendLine("            break;");
DllMain.AppendLine("    }");
DllMain.AppendLine("    return TRUE;");
DllMain.AppendLine("}");

main_cpp.AppendLine(typedefs.ToString());
main_cpp.AppendLine();
main_cpp.AppendLine(funcs.ToString());
main_cpp.AppendLine();
main_cpp.AppendLine(DllMain.ToString());
Console.WriteLine("Compiling");
//locate cl.exe and compile main.cpp to {srcFileName}.dll
var clPath = dir[0] + "\\bin\\Hostx64\\x64\\cl.exe";
var srcFileName = Path.GetFileNameWithoutExtension(args[0]);
var srcPath = Path.GetFullPath("output\\main.cpp");
var dllPath = Path.GetFullPath($"output\\{srcFileName}.dll");
// locate winapi folder
var winapiDir = new DirectoryInfo("C:\\Program Files (x86)\\Windows Kits\\10\\Include").GetDirectories();
var iD1 = winapiDir[0] + "\\um";
var iD2 = winapiDir[0] + "\\shared";
var iD3 = winapiDir[0] + "\\ucrt";
var iD4 = dir[0] + "\\include";
var winapiLibDir = new DirectoryInfo("C:\\Program Files (x86)\\Windows Kits\\10\\Lib").GetDirectories();
var lD1 = winapiLibDir[0] + "\\um\\x64";
var lD2 = dir[0] + "\\lib\\x64";
var lD3 = winapiLibDir[0] + "\\ucrt\\x64";
var memoryModulePath = Path.GetFullPath("output\\MemoryModule\\MemoryModule.c");
var arg = $"\"{srcPath}\" \"{memoryModulePath}\" /Fe\"{dllPath}\" /I\"{iD1}\" /I\"{iD2}\" /I\"{iD3}\" /I\"{iD4}\" \"{lD1}\\kernel32.lib\" \"{lD1}\\uuid.lib\" \"{lD2}\\LIBCMT.lib\" \"{lD2}\\OLDNAMES.lib\" \"{lD2}\\libvcruntime.lib\" \"{lD3}\\libucrt.lib\" /link /DLL";
File.WriteAllText("output\\compile.bat", $"\"{clPath}\" {arg}\r\npause");

Directory.CreateDirectory("output");
File.WriteAllText("output\\dll.h", dll_h.ToString());
File.WriteAllText("output\\main.cpp", main_cpp.ToString());
Directory.CreateDirectory("output\\MemoryModule");
File.WriteAllText("output\\MemoryModule\\MemoryModule.h", ShimGen.Properties.Resources.MemoryModule_h);
File.WriteAllText("output\\MemoryModule\\MemoryModule.c", ShimGen.Properties.Resources.MemoryModule_c);

//attempt to compile, sanity check/debug, basically. a blank shim has no use.
var process = new Process
{
    StartInfo =
    {
        FileName = clPath,
        Arguments = arg,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    }
};
process.Start();
//output from StandardOut as it's sent
while (!process.StandardOutput.EndOfStream)
{
    Console.WriteLine(process.StandardOutput.ReadLine());
}   
process.WaitForExit();

//open explorer to output folder
Process.Start("explorer.exe", Path.GetFullPath("output"));

//clean up source files
//if (args.Length <= 1 || args[1] != "keep")
//{
//    File.Delete("output\\dll.h");
//    File.Delete("output\\main.cpp");
//    File.Delete("output\\main.obj");
//    File.Delete("output\\MemoryModule.obj");
//    Directory.Delete("output\\MemoryModule", true);
//    File.Delete($"output\\{srcFileName}.exp");
//    File.Delete($"output\\{srcFileName}.lib");
//}