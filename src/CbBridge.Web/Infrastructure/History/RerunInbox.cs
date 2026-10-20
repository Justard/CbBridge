using CbBridge.Core.Domain;

namespace CbBridge.Web.Infrastructure.History;

/// <summary>Hands tests from the history pages to the console (same browser tab = same scope).</summary>
public sealed class RerunInbox
{
    private List<TestRunPayload> _waiting = new();
    public string? Note { get; private set; }
    public string? FrameworkUrl { get; private set; }

    public void Put(IEnumerable<TestRunPayload> payloads, string note, string? frameworkUrl = null)
    {
        _waiting = payloads.ToList();
        Note = note;
        FrameworkUrl = string.IsNullOrWhiteSpace(frameworkUrl) ? null : frameworkUrl;
    }

    public (List<TestRunPayload> Payloads, string? Note, string? FrameworkUrl) Take()
    {
        var taken = (_waiting, Note, FrameworkUrl);
        _waiting = new();
        Note = null;
        FrameworkUrl = null;
        return taken;
    }
}
