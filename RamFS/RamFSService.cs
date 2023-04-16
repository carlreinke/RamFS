// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using Fsp;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.AccessControl;
using System.Threading.Tasks;
using Tetractic.CommandLine;

internal sealed class RamFSService : Service
{
#if SYNCHRONIZED
    private const bool _synchronized = true;
#else
    private const bool _synchronized = false;
#endif

    private FileSystemHost? _host;

    public RamFSService()
        : base(nameof(RamFSService))
    {
    }

    protected override void OnStart(string[] args)
    {
        args = Pop(args);

        var rootCommand = new RootCommand("RAMFS");

        rootCommand.HelpOption = rootCommand.AddOption('h', "help", "Shows a usage summary.");

        rootCommand.VerboseOption = rootCommand.AddOption('v', "verbose", "Enable additional output.");
        rootCommand.VerboseOption.HelpVisibility = HelpVisibility.Verbose;

        var caseSensitiveOption = rootCommand.AddOption('c', "case-sensitive", "Make the file system case-sensitive.");

        var nameOption = rootCommand.AddOption('F', "file-system-name", "NAME", "Set the file system name.  (default: RAMFS)");

        var labelOption = rootCommand.AddOption('l', "label", "LABEL", "Set the drive label.  (default: RAM)");

        var securityOption = rootCommand.AddOption<RawSecurityDescriptor>('S', "security", "SDDL", "Set the security descriptor of the root directory.", TryParseSecurityDescriptor);

        var sizeOption = rootCommand.AddOption<ulong>('s', "size", "SIZE", "Set the size of the file system.  (default: 2G)", TryParseSize);

        var debugOption = rootCommand.AddOption(null, "debug", "Enables debugging output.");
        debugOption.HelpVisibility = HelpVisibility.Verbose;

        var mountPointParameter = rootCommand.AddParameter("MOUNTPOINT", "The path where the file system should be mounted.  (default: *:)\nex:  *:      Mount on an available drive letter.\nex:  R:      Mount on R drive.\nex:  \\\\.\\R:  Mount for all users on R drive.  (Administrator)\nex:  C:\\RAM  Mount on a non-existent path.");
        mountPointParameter.Optional = true;

        rootCommand.SetInvokeHandler(() =>
        {
            bool caseSensitive = caseSensitiveOption.Count > 0;
            string? name = nameOption.ValueOrDefault;
            string? label = labelOption.ValueOrDefault;
            var rootSecurityDescriptor = securityOption.ValueOrDefault;
            ulong size = sizeOption.GetValueOrDefault(1uL << 31);
            bool debug = debugOption.Count > 0;
            string? mountPoint = mountPointParameter.ValueOrDefault;

            if (size < 512)
                throw new Exception("The specified file system size is insufficient.");

            if (debug)
                _ = FileSystemHost.SetDebugLogFile("-");

            var fileSystem = new RamFS(size, caseSensitive, name, label, rootSecurityDescriptor);
            var host = new FileSystemHost(fileSystem);
            try
            {
                int result = host.Mount(mountPoint, Synchronized: _synchronized, DebugLog: debug ? ~0u : 0u);
                if (result != FileSystemBase.STATUS_SUCCESS)
                    throw new Exception($"Error 0x{result:X8}.");
            }
            catch
            {
                host.Dispose();
                throw;
            }

            _host = host;

            Console.CancelKeyPress += HandleCancelKeyPress;

            return 1;
        });

        bool stop = false;
        try
        {
            stop = rootCommand.Execute(args) != 1;
        }
        catch (InvalidCommandLineException ex)
        {
            Console.Error.WriteLine(ex.Message);
            CommandHelp.WriteHelpHint(ex.Command, Console.Error);
            stop = true;
        }
        catch (Exception ex)
        {
#if DEBUG
            Console.Error.WriteLine(ex);
#else
            Console.Error.WriteLine(ex.Message);
#endif
            stop = true;
        }
        if (stop)
        {
            Console.Error.WriteLine();
            throw null!;
        }
    }

    private bool TryParseSecurityDescriptor(string text, [MaybeNullWhen(false)] out RawSecurityDescriptor value)
    {
        try
        {
            value = new RawSecurityDescriptor(text);
            return value.Owner != null && value.Group != null;
        }
        catch (ArgumentException)
        {
            value = default;
            return false;
        }
    }

    private bool TryParseSize(string text, out ulong value)
    {
        if (text.Length == 0)
        {
            value = default;
            return false;
        }

        int shift = text[text.Length - 1] switch
        {
            'T' => 40,
            'G' => 30,
            'M' => 20,
            'K' => 10,
            _ => 0,
        };
        if (shift != 0)
            text = text.Substring(0, text.Length - 1);

        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return false;

        ulong unshiftedValue = value;
        value <<= shift;
        return (value >> shift) == unshiftedValue;
    }

    protected override void OnStop()
    {
        Console.CancelKeyPress -= HandleCancelKeyPress;

        _host!.Dispose();
        _host = null;

    }

    private static string[] Pop(string[] array)
    {
        string[] newArray = new string[array.Length - 1];
        Array.Copy(array, 1, newArray, 0, newArray.Length);
        return newArray;
    }

    private void HandleCancelKeyPress(object sender, ConsoleCancelEventArgs e)
    {
        _ = Task.Run(() => Stop());

        e.Cancel = true;
    }
}
