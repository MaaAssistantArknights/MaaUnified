using System.Collections.Generic;
using System.Linq;

namespace MAAUnified.Application.Services.WebApi;

public sealed class WebApiTaskStore
{
    private readonly List<WebApiTaskDefinition> _tasks = new();
    private readonly object _gate = new();
    public WebApiTaskDefinition Append(WebApiTaskDefinition task)
    {
        lock (_gate)
        {
            if (task.Id <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(task), "WebApi task ids must come from MaaCore.");
            }

            if (_tasks.Any(item => string.Equals(item.TaskType, task.TaskType, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A WebApi task of type `{task.TaskType}` already exists.");
            }

            _tasks.Add(task);
            return task;
        }
    }

    public bool TryModify(int id, WebApiTaskDefinition update)
    {
        lock (_gate)
        {
            var index = _tasks.FindIndex(task => task.Id == id);
            if (index < 0)
            {
                return false;
            }

            _tasks[index] = update with { Id = id };
            return true;
        }
    }

    public bool TryGet(int id, out WebApiTaskDefinition? task)
    {
        lock (_gate)
        {
            var index = _tasks.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                task = null;
                return false;
            }

            task = _tasks[index];
            return true;
        }
    }

    public IReadOnlyList<WebApiTaskDefinition> List()
    {
        lock (_gate)
        {
            return _tasks.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _tasks.Clear();
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _tasks.Count == 0;
            }
        }
    }
}
