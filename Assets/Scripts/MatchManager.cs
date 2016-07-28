﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEngine;

namespace Sanicball
{
    public class MatchPlayerEventArgs : EventArgs
    {
        public MatchPlayer Player { get; private set; }
        public bool IsLocal { get; private set; }

        public MatchPlayerEventArgs(MatchPlayer player, bool isLocal)
        {
            Player = player;
            IsLocal = isLocal;
        }
    }

    public class SettingsChangeArgs : EventArgs
    {
        public Data.MatchSettings NewSettings { get; private set; }

        public SettingsChangeArgs(Data.MatchSettings newSettings)
        {
            NewSettings = newSettings;
        }
    }

    /// <summary>
    /// Manages game state - scenes, players, all that jazz
    /// </summary>
    public class MatchManager : MonoBehaviour
    {
        [SerializeField]
        private string lobbySceneName = "Lobby";

        //Prefabs
        [SerializeField]
        private UI.PauseMenu pauseMenuPrefab;
        [SerializeField]
        private RaceManager raceManagerPrefab;

        //Match state
        private List<MatchClient> clients = new List<MatchClient>();
        private List<MatchPlayer> players = new List<MatchPlayer>();
        private Data.MatchSettings currentSettings;
        private bool inLobby = false;
        private bool lobbyTimerOn = false;
        private const float lobbyTimerMax = 3;
        private float lobbyTimer = lobbyTimerMax;
        private Guid myGuid;

        //Bools for scene initializing
        private bool loadingLobby = false;
        private bool loadingStage = false;
        private bool showSettingsOnLobbyLoad = false;

        //Events
        public event EventHandler<MatchPlayerEventArgs> MatchPlayerAdded;
        public event EventHandler<MatchPlayerEventArgs> MatchPlayerRemoved;
        public event EventHandler MatchSettingsChanged;
        public event EventHandler<SettingsChangeArgs> SettingsChangeRequested;

        //new stuff
        private Match.MatchMessenger messenger;

        public bool OnlineMode { get; private set; }

        /// <summary>
        /// Contains all players in the game, even ones from other clients in online races
        /// </summary>
        public ReadOnlyCollection<MatchPlayer> Players { get { return players.AsReadOnly(); } }
        /// <summary>
        /// Current settings for this match. On remote clients, this is only used for showing settings on the UI.
        /// </summary>
        public Data.MatchSettings CurrentSettings { get { return currentSettings; } }

        #region Match message callbacks

        private void SettingsChangedCallback(Match.SettingsChangedMessage msg)
        {
            currentSettings = msg.NewMatchSettings;
            if (MatchSettingsChanged != null)
                MatchSettingsChanged(this, EventArgs.Empty);
        }

        private void ClientJoinedCallback(Match.ClientJoinedMessage msg)
        {
            clients.Add(new MatchClient(msg.ClientGuid, msg.ClientName));
            Debug.Log("New client " + msg.ClientName);
        }

        private void PlayerJoinedCallback(Match.PlayerJoinedMessage msg)
        {
            var p = new MatchPlayer(msg.ClientGuid, msg.CtrlType, msg.InitialCharacter);
            players.Add(p);

            if (inLobby)
            {
                SpawnLobbyBall(p);
            }

            p.ChangedReady += AnyPlayerChangedReadyHandler;

            StopLobbyTimer(); //TODO: look into moving this (make the server trigger it while somehow still having it work in local play)

            if (MatchPlayerAdded != null)
                MatchPlayerAdded(this, new MatchPlayerEventArgs(p, msg.ClientGuid == myGuid));
        }

        private void PlayerLeftCallback(Match.PlayerLeftMessage msg)
        {
            var player = players.FirstOrDefault(a => a.ClientGuid == msg.ClientGuid && a.CtrlType == msg.CtrlType);
            if (player != null)
            {
                players.Remove(player);

                if (player.BallObject)
                {
                    Destroy(player.BallObject.gameObject);
                }

                if (MatchPlayerRemoved != null)
                    MatchPlayerRemoved(this, new MatchPlayerEventArgs(player, msg.ClientGuid == myGuid)); //TODO: determine if removed player was local
            }
        }

        private void CharacterChangedCallback(Match.CharacterChangedMessage msg)
        {
            if (!inLobby)
            {
                Debug.LogError("Cannot set character outside of lobby!");
            }

            var player = players.FirstOrDefault(a => a.ClientGuid == msg.ClientGuid && a.CtrlType == msg.CtrlType);
            if (player != null)
            {
                player.CharacterId = msg.NewCharacter;
                SpawnLobbyBall(player);
            }
        }

        #endregion Match message callbacks

        #region State changing methods

        public void RequestSettingsChange(Data.MatchSettings newSettings)
        {
            messenger.SendMessage(new Match.SettingsChangedMessage(newSettings));
        }

        public void RequestPlayerJoin(ControlType ctrlType, int initialCharacter)
        {
            messenger.SendMessage(new Match.PlayerJoinedMessage(myGuid, ctrlType, initialCharacter));
        }

        public void RequestPlayerLeave(ControlType ctrlType)
        {
            messenger.SendMessage(new Match.PlayerLeftMessage(myGuid, ctrlType));
        }

        public void RequestCharacterChange(ControlType ctrlType, int newCharacter)
        {
            messenger.SendMessage(new Match.CharacterChangedMessage(myGuid, ctrlType, newCharacter));
        }

        #endregion State changing methods

        #region Match initializing

        public void InitLocalMatch()
        {
            currentSettings = Data.ActiveData.MatchSettings;

            messenger = new Match.LocalMatchMessenger();

            showSettingsOnLobbyLoad = true;
            GoToLobby();
        }

        public void InitOnlineMatch(Lidgren.Network.NetClient client, Lidgren.Network.NetConnection serverConnection)
        {
            OnlineMode = true;

            //TODO: Recieve match status and sync up
            //For now lets just use default settings
            currentSettings = Data.MatchSettings.CreateDefault();

            messenger = new Match.OnlineMatchMessenger(client, serverConnection);

            showSettingsOnLobbyLoad = true;
            GoToLobby();
        }

        #endregion Match initializing

        private void Start()
        {
            DontDestroyOnLoad(gameObject);

            //A messenger should be created by now! Time to create some message listeners
            messenger.CreateListener<Match.SettingsChangedMessage>(SettingsChangedCallback);
            messenger.CreateListener<Match.ClientJoinedMessage>(ClientJoinedCallback);
            messenger.CreateListener<Match.PlayerJoinedMessage>(PlayerJoinedCallback);
            messenger.CreateListener<Match.PlayerLeftMessage>(PlayerLeftCallback);
            messenger.CreateListener<Match.CharacterChangedMessage>(CharacterChangedCallback);

            //Create this client
            myGuid = Guid.NewGuid();
            messenger.SendMessage(new Match.ClientJoinedMessage(myGuid, "client#" + myGuid));
        }

        private void Update()
        {
            messenger.UpdateListeners();

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.JoystickButton7))
            {
                if (!UI.PauseMenu.GamePaused)
                {
                    UI.PauseMenu menu = Instantiate(pauseMenuPrefab);
                    menu.OnlineMode = OnlineMode;
                }
                else
                {
                    var menu = FindObjectOfType<UI.PauseMenu>();
                    if (menu)
                        Destroy(menu.gameObject);
                }
            }

            if (inLobby && Input.GetKeyDown(KeyCode.O))
            {
                LobbyReferences.Active.MatchSettingsPanel.Show();
            }

            if (lobbyTimerOn && inLobby)
            {
                lobbyTimer -= Time.deltaTime;
                LobbyReferences.Active.CountdownField.text = "Match starts in " + Mathf.Ceil(lobbyTimer);

                if (lobbyTimer <= 0)
                {
                    GoToStage();
                    StopLobbyTimer();
                }
            }
        }

        private void AnyPlayerChangedReadyHandler(object sender, EventArgs e)
        {
            var allReady = players.TrueForAll(a => a.ReadyToRace);
            if (allReady && !lobbyTimerOn)
            {
                StartLobbyTimer();
            }
            if (!allReady && lobbyTimerOn)
            {
                StopLobbyTimer();
            }
        }

        private void StartLobbyTimer()
        {
            lobbyTimerOn = true;
            LobbyReferences.Active.CountdownField.enabled = true;
        }

        private void StopLobbyTimer()
        {
            lobbyTimerOn = false;
            lobbyTimer = lobbyTimerMax;
            LobbyReferences.Active.CountdownField.enabled = false;
        }

        #region Scene changing / race loading

        public void GoToLobby()
        {
            if (inLobby) return;

            loadingStage = false;
            loadingLobby = true;
            UnityEngine.SceneManagement.SceneManager.LoadScene(lobbySceneName);
        }

        public void GoToStage()
        {
            var targetStage = Data.ActiveData.Stages[currentSettings.StageId];

            loadingStage = true;
            loadingLobby = false;

            CameraFade.StartAlphaFade(Color.black, false, 0.3f, 0.05f, () =>
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(targetStage.sceneName);
            });
        }

        //Check if we were loading the lobby or the race
        private void OnLevelWasLoaded(int level)
        {
            if (loadingLobby)
            {
                InitLobby();
                loadingLobby = false;
                if (showSettingsOnLobbyLoad)
                {
                    //Let the player pick settings first time entering the lobby
                    LobbyReferences.Active.MatchSettingsPanel.Show();
                    showSettingsOnLobbyLoad = false;
                }
            }
            if (loadingStage)
            {
                InitRace();
                loadingStage = false;
                foreach (var p in Players)
                {
                    p.ReadyToRace = false;
                }
            }
        }

        //Initiate the lobby after loading lobby scene
        private void InitLobby()
        {
            inLobby = true;
            foreach (var p in Players)
            {
                SpawnLobbyBall(p);
            }
        }

        //Initiate a race after loading the stage scene
        private void InitRace()
        {
            inLobby = false;
            var raceManager = Instantiate(raceManagerPrefab);
            raceManager.Settings = currentSettings;
        }

        public void QuitMatch()
        {
            UnityEngine.SceneManagement.SceneManager.LoadScene("Menu");
            Destroy(gameObject);
        }

        #endregion Scene changing / race loading

        private void SpawnLobbyBall(MatchPlayer player)
        {
            var spawner = LobbyReferences.Active.BallSpawner;
            if (player.BallObject != null)
            {
                Destroy(player.BallObject.gameObject);
            }
            player.BallObject = spawner.SpawnBall(Data.PlayerType.Normal, player.CtrlType, player.CharacterId, "Player");
        }
    }

    public class MatchClient
    {
        public Guid Guid { get; private set; }
        public string Name { get; private set; }

        public MatchClient(Guid guid, string name)
        {
            Guid = guid;
            Name = name;
        }
    }

    [Serializable]
    public class MatchPlayer
    {
        private Guid clientGuid;
        private string name;
        private ControlType ctrlType;
        private bool readyToRace;

        public MatchPlayer(Guid clientGuid, ControlType ctrlType, int initialCharacterId)
        {
            this.clientGuid = clientGuid;
            this.ctrlType = ctrlType;
            CharacterId = initialCharacterId;
        }

        public event EventHandler LeftMatch;

        public event EventHandler ChangedReady;

        public Guid ClientGuid { get { return clientGuid; } }
        public ControlType CtrlType { get { return ctrlType; } }
        public int CharacterId { get; set; }
        public Ball BallObject { get; set; }
        public bool ReadyToRace
        {
            get { return readyToRace; }
            set
            {
                readyToRace = value;
                if (ChangedReady != null)
                    ChangedReady(this, EventArgs.Empty);
            }
        }
    }
}