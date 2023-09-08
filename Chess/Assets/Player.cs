using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TMPro;
using Unity.Services.Authentication;
using Unity.Services.CloudCode;
using Unity.Services.CloudCode.Subscriptions;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.InputSystem;
using WebSocketSharp;

public class Player : MonoBehaviour
{
    private GameObject _selectedPiece;
    public Camera playerCamera;
    public TextMeshProUGUI lobbyInputCodeText;
    public TextMeshProUGUI lobbyCodeText;
    public TextMeshProUGUI playerNameText;
    public GameObject uiPanel;
    public GameObject board;
    
    private readonly Dictionary<string, UnityEngine.Object> _prefabs = new();
    private Lobby _currentLobby;
    private const string StartingBoard = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private bool _gameStarted;

    private readonly Color32 _selectedColor = new (84, 84, 255, 255);
    private readonly Color32 _lightColor = new(223, 210, 194, 255);
    private readonly Color32 _darkColor = new (84, 84, 84, 255);
    
    private async void Start()
    {
        await UnityServices.InitializeAsync();
        await AuthenticationService.Instance.SignInAnonymouslyAsync();
        playerNameText.text = AuthenticationService.Instance.PlayerId;
        await SubscribeToPlayerMessages();
        SyncBoard(FenToDict(StartingBoard));
    }

    public async void CreateGame()
    {
        const int maxPlayers = 2;
        var options = new CreateLobbyOptions
        {
            IsPrivate = false,
            Player = new Unity.Services.Lobbies.Models.Player(
                id: AuthenticationService.Instance.PlayerId,
                data: new Dictionary<string, PlayerDataObject>()
                {
                    {
                        "colour", new PlayerDataObject(
                            visibility: PlayerDataObject.VisibilityOptions
                                .Member, // Visible only to members of the lobby.
                            value: "white")
                    }
                })
        };
        _currentLobby = await LobbyService.Instance.CreateLobbyAsync(lobbyCodeText.text, maxPlayers, options);
        JoinGame(_currentLobby.Id, _currentLobby.LobbyCode);
    }

    public async void JoinLobbyByCode()
    {
        try
        {
            // There's a weird no space character that gets added to the end of the lobby code, let's remove it for now
            var sanitizedLobbyCode = Regex.Replace(lobbyInputCodeText.text, @"\s", "").Replace("\u200B", "");
            _currentLobby = await LobbyService.Instance.JoinLobbyByCodeAsync(sanitizedLobbyCode);
            lobbyCodeText.text = _currentLobby.LobbyCode;
            JoinGame(_currentLobby.Id, _currentLobby.LobbyCode);
        }
        catch (LobbyServiceException exception)
        {
            Debug.LogException(exception);
        }
    }

    private async void JoinGame(string lobbyId, string lobbyCode)
    {
        try
        {
            lobbyCodeText.text = lobbyCode;
            var joinGameResponse = await CloudCodeService.Instance.CallModuleEndpointAsync<JoinGameResponse>("ChessCloudCode", "JoinGame",
                new Dictionary<string, object> { { "session", lobbyId } });
            if (!joinGameResponse.Board.IsNullOrEmpty())
            {
                SyncBoard(FenToDict(joinGameResponse.Board));
                uiPanel.SetActive(false);    
            }
            else
            {
                throw new Exception(joinGameResponse.Error);
            }
            _gameStarted = true;
        }
        catch (CloudCodeException exception)
        {
            Debug.LogException(exception);
        }
    }

    private void SyncBoard(Dictionary<Tuple<int, int>, char> boardState)
    {
        try
        {
            foreach (Transform child in board.transform)
            {
                Destroy(child.gameObject);
            }
            foreach (var piece in boardState)
            {
                
                var pieceType = char.ToLower(piece.Value) switch
                {
                    'p' => "Pawn",
                    'n' => "Knight",
                    'b' => "Bishop",
                    'r' => "Rook",
                    'q' => "Queen",
                    'k' => "King",
                    _ => ""
                };
                var prefabName = pieceType + (char.IsUpper(piece.Value) ? "Light" : "Dark");
                if (!_prefabs.TryGetValue(prefabName, out var prefab))
                {
                    _prefabs[prefabName] = Resources.Load($"{pieceType}/Prefabs/{prefabName}");    
                }
                
                var newObject = Instantiate(_prefabs[prefabName], board.transform);
                newObject.GameObject().transform.position = new Vector3(piece.Key.Item1, 0, piece.Key.Item2);
                newObject.GameObject().transform.rotation = Quaternion.Euler(0, char.IsLower(piece.Value)? 180 : 0, 0);
            }
        }
        catch (CloudCodeException exception)
        {
            Debug.LogException(exception);
        }
    }

    private async void MakeMove(GameObject piece, Vector3 toPos)
    {
        if (piece == null) return;
        try
        {
            var result = await CloudCodeService.Instance.CallModuleEndpointAsync("ChessCloudCode", "MakeMove",
                new Dictionary<string, object>
                    { { "session", _currentLobby.Id }, { "fromPosition", PosToFen(piece.transform.position) }, { "toPosition", PosToFen(toPos) } });
            Debug.Log(result);
            SelectPiece(null);
            var response = JsonConvert.DeserializeObject<Dictionary<string, string>>(result);
            SyncBoard(FenToDict(response["board"]));
        }
        catch (CloudCodeException exception)
        {
            Debug.LogException(exception);
        }
    }

    private Task SubscribeToPlayerMessages()
    {
        var callbacks = new SubscriptionEventCallbacks();
        callbacks.MessageReceived += @event =>
        {
            switch (@event.MessageType)
            {
                case "boardUpdated":
                {
                    var message = JsonConvert.DeserializeObject<Dictionary<string, string>>(@event.Message);
                    SyncBoard(FenToDict(message["board"]));
                    break;
                }
                case "clearBoard":
                {
                    SyncBoard(FenToDict(StartingBoard));
                    break;
                }
                default:
                    Debug.Log($"Got unsupported player Message: {JsonConvert.SerializeObject(@event, Formatting.Indented)}");
                    break;
            }
        };
        callbacks.ConnectionStateChanged += @event =>
        {
            if (@event == EventConnectionState.Subscribed && _currentLobby != null && _gameStarted)
            {
                JoinGame(_currentLobby.Id, _currentLobby.LobbyCode);
            }
            Debug.Log($"Got player subscription ConnectionStateChanged: {@event.ToString()}");
        };
        callbacks.Kicked += () =>
        {
            Debug.Log($"Got player subscription Kicked");
        };
        callbacks.Error += @event =>
        {
            Debug.Log($"Got player subscription Error: {JsonConvert.SerializeObject(@event, Formatting.Indented)}");
        };
        return CloudCodeService.Instance.SubscribeToPlayerMessagesAsync(callbacks);
    }

    public void PlayerInteract(InputAction.CallbackContext context)
    {
        if (!context.performed && _currentLobby != null) return;
        var mousePosition = Mouse.current.position.ReadValue();
        var rayOrigin = playerCamera.ScreenPointToRay(mousePosition);
        if (Physics.Raycast(rayOrigin, out var hitInfo))
        {
            // TODO: check if a piece is selected and another piece is clicked - is that a move?
            if (hitInfo.transform.gameObject.name == "Board")
            {
                var boardPos = new Vector3(Mathf.RoundToInt(hitInfo.point.x), 0, Mathf.RoundToInt(hitInfo.point.z));
                MakeMove(_selectedPiece, boardPos);
            }
            else
            {
                SelectPiece(hitInfo.transform.gameObject);
                Debug.Log($"Piece selected: {_selectedPiece.name}");    
            }
        }
        else
        {
            SelectPiece(null);   
        }
    }

    private void SelectPiece(GameObject piece)
    {
        if (_selectedPiece != null)
        {
            ChangeMaterialColor(_selectedPiece,
                _selectedPiece.name.Contains("Light") ? _lightColor : _darkColor);
        }
        _selectedPiece = piece;
        if (_selectedPiece == null) return;
        ChangeMaterialColor(_selectedPiece, _selectedColor);
    }

    private static Dictionary<Tuple<int, int>, char> FenToDict(string fen)
    {
        var fenParts = fen.Split(' ');
        var boardState = fenParts[0];
        var ranks = boardState.Split('/');

        var coordinatesDict = new Dictionary<Tuple<int, int>, char>();
        var x = 0;
        var y = 7;

        foreach (var rank in ranks)
        {
            foreach (var c in rank)
            {
                if (char.IsDigit(c))
                {
                    x += int.Parse(c.ToString());
                }
                else
                {
                    var coordinates = new Tuple<int, int>(x, y);
                    coordinatesDict.Add(coordinates, c);
                    x += 1;
                }
            }
            x = 0;
            y -= 1;
        }

        return coordinatesDict;
    }

    private void ChangeMaterialColor(GameObject obj, Color newColor)
    {
        var selectedRenderer = obj.GetComponent<Renderer>();
        selectedRenderer.material.color = newColor;
    }
    
    private class JoinGameResponse
    {
        public string Board { get; set; }
        public string Error { get; set; }
    }

    private string PosToFen(Vector3 pos)
    {
        return (char)(pos.x + 97) + ((char)pos.z + 1).ToString();
    }
}