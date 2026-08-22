using ArbitrageBot.Services;
using Microsoft.AspNetCore.SignalR;

namespace ArbitrageBot.Hubs;

public class ArbitrageHub : Hub
{
    private readonly ArbitrageState _state;

    public ArbitrageHub(ArbitrageState state)
    {
        _state = state; 
    }

    public override async Task OnConnectedAsync()
    {
        // Push current snapshot immediately on connect
        await Clients.Caller.SendAsync("Snapshot", _state.GetSnapshot());
        await base.OnConnectedAsync();
    }

    public Task RequestSnapshot()
    {
        return Clients.Caller.SendAsync("Snapshot", _state.GetSnapshot());
    }
}
