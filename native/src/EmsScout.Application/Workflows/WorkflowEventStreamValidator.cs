namespace EmsScout.Application.Workflows;

public sealed class WorkflowEventStreamValidator
{
    private string? _workflowId;
    private long _lastSequence;
    private bool _started;
    private bool _terminal;

    public bool IsComplete => _started && _terminal;

    public WorkflowTerminalOutcome? Outcome { get; private set; }

    public void Accept(WorkflowEventV1 workflowEvent)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        if (_terminal)
        {
            throw new WorkflowEventParseException("Workflow event received after the terminal event.");
        }

        if (!_started)
        {
            if (workflowEvent.Type != WorkflowEventType.Started || workflowEvent.Sequence != 1)
            {
                throw new WorkflowEventParseException("Workflow event stream must start with started at seq 1.");
            }
            _workflowId = workflowEvent.WorkflowId;
            _started = true;
        }
        else
        {
            if (!workflowEvent.WorkflowId.Equals(_workflowId, StringComparison.Ordinal))
            {
                throw new WorkflowEventParseException("Workflow event stream changed workflowId.");
            }
            if (workflowEvent.Type == WorkflowEventType.Started)
            {
                throw new WorkflowEventParseException("Workflow event stream contains more than one started event.");
            }
            if (workflowEvent.Sequence != _lastSequence + 1)
            {
                throw new WorkflowEventParseException(
                    $"Workflow event seq must be contiguous; expected {_lastSequence + 1}, got {workflowEvent.Sequence}.");
            }
        }

        _lastSequence = workflowEvent.Sequence;
        if (workflowEvent.Type == WorkflowEventType.Terminal)
        {
            _terminal = true;
            Outcome = workflowEvent.Outcome;
        }
    }

    public void EnsureComplete()
    {
        if (!IsComplete)
        {
            throw new WorkflowEventParseException("Workflow event stream ended without one terminal event.");
        }
    }
}

public static class WorkflowEventStreamParser
{
    public static IReadOnlyList<WorkflowEventV1> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var events = new List<WorkflowEventV1>();
        var validator = new WorkflowEventStreamValidator();
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            try
            {
                var workflowEvent = WorkflowEventParser.Parse(line);
                validator.Accept(workflowEvent);
                events.Add(workflowEvent);
            }
            catch (WorkflowEventParseException error)
            {
                throw new WorkflowEventParseException($"Invalid WorkflowEvent NDJSON at line {lineNumber}: {error.Message}", error);
            }
        }

        validator.EnsureComplete();
        return events;
    }
}
