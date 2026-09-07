using System.Diagnostics;

namespace DiTunnel.Infrastructure.Xray;

public sealed class XrayProcessManager : IXrayProcessManager
{
    private readonly XrayOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _processLock = new();
    private Process? _process;
    private bool _disposed;

    public XrayProcessManager(XrayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            throw new ArgumentException("Путь к Xray-core не задан.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            throw new ArgumentException("Рабочая папка Xray-core не задана.", nameof(options));
        }

        if (options.StartupGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Время запуска не может быть отрицательным.");
        }

        if (options.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Таймаут остановки должен быть положительным.");
        }

        _options = options;
    }

    public bool IsRunning
    {
        get
        {
            lock (_processLock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_processLock)
            {
                return _process is { HasExited: false } process ? process.Id : null;
            }
        }
    }

    public event Action<XrayLogEntry>? LogReceived;

    public event Action<int>? Exited;

    public async Task<XrayValidationResult> ValidateConfigurationAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureNotRunning();
            return await ValidateCoreAsync(configurationPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureNotRunning();

            if (_options.ValidateConfigurationBeforeStart)
            {
                var validationResult = await ValidateCoreAsync(configurationPath, cancellationToken).ConfigureAwait(false);
                if (!validationResult.IsValid)
                {
                    throw new XrayConfigurationException(validationResult);
                }
            }

            var fullConfigurationPath = ResolveConfigurationPath(configurationPath);
            var process = CreateProcess(CreateStartInfo(fullConfigurationPath, validateOnly: false));
            process.OutputDataReceived += OnOutputDataReceived;
            process.ErrorDataReceived += OnErrorDataReceived;

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Не удалось запустить Xray-core.");
            }

            lock (_processLock)
            {
                _process = process;
            }

            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await Task.Delay(_options.StartupGracePeriod, cancellationToken).ConfigureAwait(false);

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Xray-core завершился во время запуска с кодом {process.ExitCode}.");
            }
        }
        catch
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_disposed)
            {
                return;
            }

            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<XrayValidationResult> ValidateCoreAsync(
        string configurationPath,
        CancellationToken cancellationToken)
    {
        var fullConfigurationPath = ResolveConfigurationPath(configurationPath);
        using var process = CreateProcess(CreateStartInfo(fullConfigurationPath, validateOnly: true));

        if (!process.Start())
        {
            throw new InvalidOperationException("Не удалось запустить проверку конфигурации Xray-core.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        return new XrayValidationResult(
            process.ExitCode == 0,
            process.ExitCode,
            standardOutput,
            standardError);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Process? process;

        lock (_processLock)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();

                using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);

                try
                {
                    await process.WaitForExitAsync(combined.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        finally
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Exited -= OnProcessExited;
            process.Dispose();
        }
    }

    private ProcessStartInfo CreateStartInfo(string configurationPath, bool validateOnly)
    {
        EnsureRuntimeExists();

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(_options.ExecutablePath),
            WorkingDirectory = Path.GetFullPath(_options.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("run");
        if (validateOnly)
        {
            startInfo.ArgumentList.Add("-test");
        }

        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(configurationPath);
        return startInfo;
    }

    private static Process CreateProcess(ProcessStartInfo startInfo)
    {
        return new Process { StartInfo = startInfo };
    }

    private string ResolveConfigurationPath(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var fullPath = Path.GetFullPath(configurationPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Конфигурация Xray-core не найдена.", fullPath);
        }

        return fullPath;
    }

    private void EnsureRuntimeExists()
    {
        var executablePath = Path.GetFullPath(_options.ExecutablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Исполняемый файл Xray-core не найден.", executablePath);
        }

        var workingDirectory = Path.GetFullPath(_options.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Рабочая папка Xray-core не найдена: {workingDirectory}");
        }
    }

    private void EnsureNotRunning()
    {
        lock (_processLock)
        {
            if (_process is null)
            {
                return;
            }

            if (!_process.HasExited)
            {
                throw new InvalidOperationException("Xray-core уже запущен.");
            }

            _process.OutputDataReceived -= OnOutputDataReceived;
            _process.ErrorDataReceived -= OnErrorDataReceived;
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs args)
    {
        PublishLog(XrayLogStream.StandardOutput, args.Data);
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
    {
        PublishLog(XrayLogStream.StandardError, args.Data);
    }

    private void PublishLog(XrayLogStream stream, string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            LogReceived?.Invoke(new XrayLogEntry(DateTimeOffset.UtcNow, stream, message));
        }
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (sender is not Process process)
        {
            return;
        }

        Exited?.Invoke(process.ExitCode);
    }
}
