﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace BHungerGaemsBot
{
    public class BotGameInstance
    {
        public const string Smiley = "😃"; // :smiley:
        public const string Smile = "😄"; // :smile:
        public const string ReactionToUse = Smiley;
        public const string ReactionToUseText = ":smiley:(Smiley)";

        private readonly object _syncObj = new object();
        private Dictionary<Player, Player> _players;
        private Dictionary<Player, List<string>> _cheatingPlayers;
        private bool _cancelGame;

        private ulong _messageId;
        private IMessageChannel _channel;
        private int _testUsers;

        public void LogAndReply(string message)
        {
            Logger.LogInternal(message);
            _channel.SendMessageAsync(message);
        }

        public void LogAndReplyError(string message, string method)
        {
            Logger.Log(new LogMessage(LogSeverity.Error, method, message));
            _channel.SendMessageAsync(message);
        }

        private Task HandleReactionAdded(Cacheable<IUserMessage, ulong> msg, ISocketMessageChannel channel, SocketReaction reaction)
        {
            try
            {
                if (reaction != null && reaction.User.IsSpecified) // for now except all reactions && reaction.Emote.Name == ReactionToUse)
                {
                    Player player = new Player(reaction.User.Value);
                    lock (_syncObj)
                    {
                        if (msg.Value?.Id == _messageId)
                        {
                            // check for changed NickName
                            List<string> fullUserNameList;
                            if (_cheatingPlayers.TryGetValue(player, out fullUserNameList) && fullUserNameList != null && fullUserNameList.Count > 1)
                            {
                                if (fullUserNameList.Contains(player.FullUserName) == false)
                                {
                                    fullUserNameList.Add(player.FullUserName);
                                }
                            }
                            else
                            {
                                Player existingPlayer;
                                if (_players.TryGetValue(player, out existingPlayer))
                                {
                                    if  (player.FullUserName.Equals(existingPlayer.FullUserName) == false)
                                    {
                                        _players.Remove(player);
                                        _cheatingPlayers[player] = new List<string> { existingPlayer.FullUserName, player.FullUserName };
                                    }
                                }
                                else
                                {
                                    _players[player] = player;
                                }
                            }                            
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(new LogMessage(LogSeverity.Error, "HandleReactionAdded", "Unexpected Exception", ex));
            }
            return Task.CompletedTask;
        }
/*
        private Task HandleReactionRemoved(Cacheable<IUserMessage, ulong> msg, ISocketMessageChannel channel, SocketReaction reaction)
        {
            try
            {
                if (reaction != null && reaction.User.IsSpecified)
                {
                    SocketGuildUser user = reaction.User.Value as SocketGuildUser;
                    string playerName = user?.Nickname ?? reaction.User.Value.Username;
                    lock (_syncObj)
                    {
                        if (msg.Value?.Id == _messageId)
                        {
                            _players.Remove(playerName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(new LogMessage(LogSeverity.Error, "HandleReactionRemoved", "Unexpected Exception", ex));
            }
            return Task.CompletedTask;
        }
*/
        public void AbortGame()
        {
            lock (_syncObj)
            {
                _cancelGame = true;
            }
        }

        private bool GetCancelGame()
        {
            lock (_syncObj)
            {
                return _cancelGame;
            }
        }

        public bool StartGame(int numWinners, int maxUsers, int maxMinutesToWait, int secondsDelayBetweenDays, IMessageChannel channel, string userName, int testUsers)
        {
            _testUsers = testUsers;

            Task.Run(() => RunGame(numWinners, maxUsers, maxMinutesToWait, secondsDelayBetweenDays, channel, userName, false));
            return true;
        }

        private void CheckReactionUsers(IUserMessage gameMessage, Dictionary<string, string> newPlayersNickNameLookup)
        {
            int eventReactionsCount = newPlayersNickNameLookup.Count;
            int badGetReactions = 0;
            int addedGetReactions = 0;
            int existingGetReactions = 0;
            int getReactionsCount = 0;
            var result = gameMessage.GetReactionUsersAsync(ReactionToUse, DiscordConfig.MaxUsersPerBatch).GetAwaiter().GetResult();
            if (result != null)
            {
                Dictionary<string, string> playersUserNameLookup = new Dictionary<string, string>();
                foreach (KeyValuePair<string, string> keyValuePair in newPlayersNickNameLookup)
                {
                    playersUserNameLookup[keyValuePair.Value] = keyValuePair.Key;
                }

                getReactionsCount = result.Count;
                foreach (IUser user in result)
                {
                    if (playersUserNameLookup.ContainsKey(user.Username) == false)
                    {
                        SocketGuildUser userLookup = _channel.GetUserAsync(user.Id).GetAwaiter().GetResult() as SocketGuildUser;
                        if (userLookup != null)
                        {
                            newPlayersNickNameLookup[userLookup.Nickname ?? userLookup.Username] = userLookup.Username;
                            addedGetReactions++;
                        }
                        else
                        {
                            badGetReactions++;
                        }
                    }
                    else
                    {
                        existingGetReactions++;
                    }
                }
            }
            Logger.Log($"RunGame - GetReactionsReturned: {getReactionsCount} EventReactions: {eventReactionsCount} BadUsers: {badGetReactions} AddedUsers: {addedGetReactions} ExistingUsers: {existingGetReactions} TotalPlayers: {newPlayersNickNameLookup.Count}");
        }

        private void RunGame(int numWinners, int maxUsers, int maxMinutesToWait, int secondsDelayBetweenDays, IMessageChannel channel, string userName, bool startWhenMaxUsers = true)
        {
            bool removeHandler = false;
            try
            {
                _channel = channel;

                string gameMessageText = $"Preparing to start a Game for ```Markdown\r\n<{userName}> in {maxMinutesToWait} minutes" 
                    + ( startWhenMaxUsers ? $" or when we get {maxUsers} reactions!" : $"!  At the start of the game the # of players will be reduced to {maxUsers} if needed.") + "```\r\n"
                    + $"React to this message with the {ReactionToUseText} emoji to enter!  Multiple Reactions(emojis) will NOT enter you more than once.\r\nPlayer entered: ";
                Task<IUserMessage> messageTask = channel.SendMessageAsync(gameMessageText + "0");
                Logger.Log(gameMessageText);
                messageTask.Wait();

                if (messageTask.IsFaulted)
                {
                    LogAndReplyError("Error getting players.", "RunGame");
                    return;
                }
                var gameMessage = messageTask.Result;
                if (gameMessage == null)
                {
                    LogAndReplyError("Error accessing Game Message.", "RunGame");
                    return;
                }
                lock (_syncObj)
                {
                    _players = new Dictionary<Player, Player>();
                    _cheatingPlayers = new Dictionary<Player, List<string>>();
                    _messageId = gameMessage.Id;
                }

                Bot.DiscordClient.ReactionAdded += HandleReactionAdded;
                //Bot.DiscordClient.ReactionRemoved += HandleReactionRemoved;
                removeHandler = true;

                Dictionary<Player, Player> newPlayersUserNameLookup;
                Dictionary<Player, List<string>> newCheatingPlayersLookup;
                DateTime now = DateTime.Now;
                int secondCounter = 0;
                int lastPlayerCount = 0;
                while (true)
                {
                    int currentPlayerCount;
                    lock (_syncObj)
                    {
                        if (_cancelGame)
                            return;
                        currentPlayerCount = _players.Count;
                        if (startWhenMaxUsers && currentPlayerCount >= maxUsers)
                        {
                            _messageId = 0;
                            newPlayersUserNameLookup = _players;
                            newCheatingPlayersLookup = _cheatingPlayers;
                            _players = new Dictionary<Player, Player>();
                            _cheatingPlayers = new Dictionary<Player, List<string>>();
                            break;
                        }
                    }
                    if (secondCounter > 10)
                    {
                        secondCounter = 0;
                        if (currentPlayerCount != lastPlayerCount)
                        {
                            lastPlayerCount = currentPlayerCount;
                            gameMessage.ModifyAsync(x => x.Content = gameMessageText + currentPlayerCount); // + "```\r\n");
                        }
                    }

                    Thread.Sleep(1000);
                    secondCounter++;
                    if ((DateTime.Now - now).TotalMinutes >= maxMinutesToWait)
                    {
                        lock (_syncObj)
                        {
                            if (_cancelGame)
                                return;
                            _messageId = 0;
                            newPlayersUserNameLookup = _players;
                            newCheatingPlayersLookup = _cheatingPlayers;
                            _players = new Dictionary<Player, Player>();
                            _cheatingPlayers = new Dictionary<Player, List<string>>();
                        }
                        break;
                    }
                }

                Bot.DiscordClient.ReactionAdded -= HandleReactionAdded;
                //Bot.DiscordClient.ReactionRemoved -= HandleReactionRemoved;
                removeHandler = false;

                //CheckReactionUsers(gameMessage, newPlayersNickNameLookup);

                // for now we don't use this anymore so don't update it.
                //lock (_syncObj)
                //{
                //    _players = new Dictionary<string, string>(newPlayersNickNameLookup);
                //}

                List<Player> players;
                if (_testUsers > 0)
                {
                    players = new List<Player>(_testUsers);
                    for (int i = 0; i < _testUsers; i++)
                        players.Add(new Player(i));
                }
                else
                {
                    players = new List<Player>(newPlayersUserNameLookup.Values);
                    if (players.Count < 1)
                    {
                        LogAndReplyError("Error, no players reacted.", "RunGame");
                        return;
                    }
                }

                if (lastPlayerCount != players.Count)
                {
                    gameMessage.ModifyAsync(x => x.Content = gameMessageText + players.Count); //  + "```\r\n"
                }

                if (newCheatingPlayersLookup.Count > 0)
                {
                    StringBuilder sb = new StringBuilder(2000);
                    foreach (KeyValuePair<Player, List<string>> pair in newCheatingPlayersLookup)
                    {
                        sb.Append($"(ID:<{pair.Key.UserId}>): ");
                        foreach (string fullUserName in pair.Value)
                        {
                            sb.Append($"<{fullUserName}> ");
                        }
                        sb.Append("\r\n");
                    }
                    LogToChannel("Players REMOVED from game due to multiple NickNames:\r\n" + sb, null);
                }

                new BHungerGames().Run(numWinners, secondsDelayBetweenDays, players, LogToChannel, GetCancelGame, startWhenMaxUsers ? 0 : maxUsers);
            }
            catch (Exception ex)
            {
                Logger.Log(new LogMessage(LogSeverity.Error, "RunGame", "Unexpected Exception", ex));
                try
                {
                    LogAndReply("Error Starting Game.");
                }
                catch (Exception ex2)
                {
                    Logger.Log(new LogMessage(LogSeverity.Error, "RunGame", "Unexpected Exception Sending Error to Discord", ex2));
                }
            }
            finally
            {
                try
                {
                    if (removeHandler)
                    {
                        Bot.DiscordClient.ReactionAdded -= HandleReactionAdded;
                        //Bot.DiscordClient.ReactionRemoved -= HandleReactionRemoved;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(new LogMessage(LogSeverity.Error, "RunGame", "Unexpected Exception In Finally", ex));
                }
                try
                {
                    BaseCommands.RemoveChannelCommandInstance(channel.Id);
                }
                catch (Exception ex)
                {
                    Logger.Log(new LogMessage(LogSeverity.Error, "RunGame", "Unexpected Exception In Finally2", ex));
                }
                try
                {
                    string cancelMessage = null;
                    lock (_syncObj)
                    {
                        if (_cancelGame)
                        {
                            cancelMessage = "GAME CANCELLED!!!";
                        }
                    }
                    if (cancelMessage != null)
                    {
                        LogAndReply(cancelMessage);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(new LogMessage(LogSeverity.Error, "RunGame", "Unexpected Exception In Finally3", ex));
                }
            }
        }

        private void SendMarkdownMsg(string msg)
        {
            _channel.SendMessageAsync("```Markdown\r\n" + msg + "```\r\n").GetAwaiter().GetResult();
        }

        private bool ChrEqualToBreakableChr(char chr)
        {
            return chr == ' ' || chr == '\n' || chr == '\r' || chr == '\t';
        }
        private int GetSizeOfFirstBreakableChr(string msg, int startingSize) // space, tabe \r \n are breakable characters.
        {
            while (ChrEqualToBreakableChr(msg[startingSize-1]) == false)
            {
                startingSize--;
            }
            return startingSize;
        }

        private void LogToChannel(string msg, string logMsgOnly)
        {
            const int maxMessageSize = 1930; // 2000 minus markdown used below.
            if (string.IsNullOrEmpty(logMsgOnly) == false)
            {
                Logger.LogInternal("LogMsgOnly: " + logMsgOnly);
            }
            Logger.LogInternal(msg);

            if (msg.Length > maxMessageSize)
            {
                while (msg.Length > maxMessageSize)
                {
                    int partSize = GetSizeOfFirstBreakableChr(msg, maxMessageSize);
                    SendMarkdownMsg(msg.Substring(0, partSize));
                    msg = msg.Substring(partSize);
                }
                SendMarkdownMsg(msg);
            }
            else
            {
                SendMarkdownMsg(msg);
            }
        }
    }
}
