using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Photon.Realtime;

public class LobbyManager : MonoBehaviour, INetworkRunnerCallbacks
{
    private enum ConnectionState
    {
        NotConnected,
        Connecting,
        InLobby,
        InRoom
    }

    [Header("Lobby UI")]
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private TMP_Dropdown lobbyDropdown;
    [SerializeField] private Button joinLobbyButton;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_InputField playerNameInput;

    [Header("Create room UI")]
    [SerializeField] private TMP_InputField roomNameInput;
    [SerializeField] private TMP_InputField maxPlayersInput;
    [SerializeField] private Button createRoomButton;

    [Header("Room list UI")]
    [SerializeField] private Transform roomListContent;
    [SerializeField] private Button roomRowTemplate;

    [Header("Room UI")]
    [SerializeField] private GameObject roomPanel;
    [SerializeField] private TMP_Text playerListText;
    [SerializeField] private Button leaveRoomButton;

    [Header("Lobby settings")]
    [SerializeField] private List<string> lobbyNames = new List<string> { "Lobby A", "Lobby B", "Lobby C" };

    private readonly List<SessionInfo> _sessions = new List<SessionInfo>();
    private readonly List<Button> _roomRows = new List<Button>();

    private NetworkRunner _runner;
    private ConnectionState _state = ConnectionState.NotConnected;
    private string _currentLobbyName;
    private AuthenticationValues _authValues;

    private void Start()
    {
        lobbyDropdown.ClearOptions();
        lobbyDropdown.AddOptions(lobbyNames);

        joinLobbyButton.onClick.AddListener(JoinSelectedLobby);
        createRoomButton.onClick.AddListener(CreateRoom);
        leaveRoomButton.onClick.AddListener(LeaveRoom);

        SetState(ConnectionState.NotConnected, "Not connected");
    }

    //connects to the lobby selected in the dropdown
    private async void JoinSelectedLobby()
    {
        string playerName = playerNameInput.text.Trim();

        if (string.IsNullOrEmpty(playerName))
        {
            statusText.text = "Enter your name";
            return;
        }

        //the random part keeps the ID unique when two players pick the same name
        _authValues = new AuthenticationValues { UserId = $"{playerName}#{Guid.NewGuid().ToString("N").Substring(0, 6)}" };
        _currentLobbyName = lobbyNames[lobbyDropdown.value];

        SetState(ConnectionState.Connecting, $"Connecting to {_currentLobbyName}...");

        CreateRunner();

        StartGameResult result = await _runner.JoinSessionLobby(SessionLobby.Custom, _currentLobbyName, _authValues);

        if (result.Ok)
        {
            SetState(ConnectionState.InLobby, $"Joined {_currentLobbyName}");
        }
        else
        {
            Debug.LogError($"Failed to join lobby: {result.ShutdownReason} - {result.ErrorMessage}");
            SetState(ConnectionState.NotConnected, $"Failed to join lobby: {result.ShutdownReason}");

            if (_runner != null)
            {
                await _runner.Shutdown();
            }
        }
    }

    //checks the input before creating the room
    private void CreateRoom()
    {
        string roomName = roomNameInput.text.Trim();

        if (string.IsNullOrEmpty(roomName))
        {
            statusText.text = "Enter a room name";
            return;
        }

        if (!int.TryParse(maxPlayersInput.text, out int maxPlayers) || maxPlayers < 1)
        {
            statusText.text = "Enter a max player count of at least 1";
            return;
        }

        //room names are unique across all lobbies so the lobby name is added to keep them separate
        //so same room name can be made in different lobbies
        string sessionName = $"{_currentLobbyName}: {roomName}";

        if (_sessions.Exists(session => session.Name == sessionName))
        {
            statusText.text = $"A room named {roomName} already exists";
            return;
        }

        StartRoom(sessionName, maxPlayers);
    }

    private async void StartRoom(string roomName, int? maxPlayers)
    {
        SetState(ConnectionState.Connecting, $"Connecting to room {roomName}...");

        //customLobbyName links the room to the current lobby so other players in that lobby can see it
        StartGameResult result = await _runner.StartGame(new StartGameArgs
        {
            GameMode = GameMode.Shared,
            SessionName = roomName,
            PlayerCount = maxPlayers,
            CustomLobbyName = _currentLobbyName,
            AuthValues = _authValues,
            OnGameStarted = OnRoomStarted
        });

        if (!result.Ok)
        {
            Debug.LogError($"Failed to join room: {result.ShutdownReason} - {result.ErrorMessage}");
            SetState(ConnectionState.NotConnected, $"Failed to join room: {result.ShutdownReason}");
        }
    }

    private void OnRoomStarted(NetworkRunner runner)
    {
        SetState(ConnectionState.InRoom, $"In room {runner.SessionInfo.Name}");
        UpdatePlayerList(runner);
    }

    private async void LeaveRoom()
    {
        SetState(ConnectionState.Connecting, "Leaving room...");
        await _runner.Shutdown();
    }

    //a networkrunner can't be reused after it shuts down so each connection gets a new one
    private void CreateRunner()
    {
        GameObject runnerObject = new GameObject("NetworkRunner");
        _runner = runnerObject.AddComponent<NetworkRunner>();
        _runner.AddCallbacks(this);
    }

    private void SetState(ConnectionState state, string message)
    {
        //fausion can report a shutdown after Unity has already destroyed the UI
        if (statusText == null)
        {
            return;
        }

        _state = state;
        statusText.text = message;

        lobbyPanel.SetActive(state != ConnectionState.InRoom);
        roomPanel.SetActive(state == ConnectionState.InRoom);

        RefreshButtons();
        RefreshRoomList();
    }

    private void RefreshButtons()
    {
        bool canJoinLobby = _state == ConnectionState.NotConnected;
        bool canUseRooms = _state == ConnectionState.InLobby;

        lobbyDropdown.interactable = canJoinLobby;
        joinLobbyButton.interactable = canJoinLobby;
        playerNameInput.interactable = canJoinLobby;

        roomNameInput.interactable = canUseRooms;
        maxPlayersInput.interactable = canUseRooms;
        createRoomButton.interactable = canUseRooms;

        leaveRoomButton.interactable = _state == ConnectionState.InRoom;
    }

    //rebuilds the room list from the latest sessions
    private void RefreshRoomList()
    {
        foreach (Button row in _roomRows)
        {
            Destroy(row.gameObject);
        }

        _roomRows.Clear();

        foreach (SessionInfo session in _sessions)
        {
            Button row = Instantiate(roomRowTemplate, roomListContent);
            row.gameObject.SetActive(true);
            //each row is cloned at runtime so its label can only be reached through the clone
            row.GetComponentInChildren<TMP_Text>().text = $"{session.Name} ({session.PlayerCount}/{session.MaxPlayers})";

            bool roomHasSpace = session.IsOpen && session.PlayerCount < session.MaxPlayers;
            row.interactable = _state == ConnectionState.InLobby && roomHasSpace;

            string roomName = session.Name;
            row.onClick.AddListener(() => StartRoom(roomName, null));

            _roomRows.Add(row);
        }
    }

    //the leaving player is skipped because ActivePlayers can still include them
    private void UpdatePlayerList(NetworkRunner runner, PlayerRef? playerWhoLeft = null)
    {
        List<string> lines = new List<string>();

        foreach (PlayerRef player in runner.ActivePlayers)
        {
            if (player == playerWhoLeft)
            {
                continue;
            }

            string suffix = player == runner.LocalPlayer ? " (You)" : "";
            //the name is the part of the user ID before the random code
            string userId = runner.GetPlayerUserId(player);
            int separator = userId != null ? userId.LastIndexOf('#') : -1;
            string name = separator > 0 ? userId.Substring(0, separator) : $"Player {player.PlayerId}";
            lines.Add($"{name}{suffix}");
        }

        playerListText.text = $"Players ({lines.Count}/{runner.SessionInfo.MaxPlayers})\n\n{string.Join("\n", lines)}";
    }

    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
    {
        _sessions.Clear();
        _sessions.AddRange(sessionList);
        RefreshRoomList();
    }

    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        UpdatePlayerList(runner);
    }

    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        UpdatePlayerList(runner, player);
    }

    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
    {
        _runner = null;
        _sessions.Clear();

        if (_state != ConnectionState.NotConnected)
        {
            string message = shutdownReason == ShutdownReason.Ok ? "Not connected" : $"Disconnected: {shutdownReason}";
            SetState(ConnectionState.NotConnected, message);
        }
    }

    public void OnConnectedToServer(NetworkRunner runner) { }
    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
    public void OnInput(NetworkRunner runner, NetworkInput input) { }
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    public void OnSceneLoadDone(NetworkRunner runner) { }
    public void OnSceneLoadStart(NetworkRunner runner) { }
}
