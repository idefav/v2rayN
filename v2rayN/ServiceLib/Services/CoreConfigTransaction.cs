namespace ServiceLib.Services;

/// <summary>Retains both originals until startup and cleanup have a confirmed outcome.</summary>
internal sealed class CoreConfigTransaction
{
    private readonly List<(string Candidate, string Target, string? Backup)> _files = [];
    private readonly Action<string, string> _replace;
    internal CoreConfigTransaction(IEnumerable<(string Candidate, string Target)> files, Action<string, string>? replace = null)
    {
        _replace = replace ?? ((source, target) => File.Move(source, target, true));
        try
        {
            foreach (var (candidate, target) in files)
            {
                var backup = File.Exists(target) ? target + ".rollback-" + Guid.NewGuid().ToString("N") : null;
                if (backup != null) File.Copy(target, backup);
                _files.Add((candidate, target, backup));
            }
        }
        catch { Commit(); throw; }
    }
    internal void Publish()
    {
        foreach (var file in _files) _replace(file.Candidate, file.Target);
    }
    internal void Rollback()
    {
        var errors = new List<Exception>();
        foreach (var file in _files)
            try
            {
                if (file.Backup == null) File.Delete(file.Target);
                else File.Copy(file.Backup, file.Target, true);
            }
            catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException("配置恢复未完成 / Configuration rollback incomplete", errors);
        Commit();
    }
    internal void Commit()
    {
        foreach (var file in _files)
            if (file.Backup != null)
                try { File.Delete(file.Backup); }
                catch (Exception ex) { Logging.SaveLog("Configuration backup cleanup", ex); }
    }
}
