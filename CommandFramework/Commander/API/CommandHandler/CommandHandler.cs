﻿// <copyright file="CommandHandler.cs" company="ArcticWalrus">
// Copyright (c) ArcticWalrus. All rights reserved.
// </copyright>

namespace CommandFramework.Managers.Models
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;
    using System.Diagnostics.Tracing;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using CommandFramework.API;
    using CommandFramework.API.CommandHandler;
    using CommandFramework.API.Enums;
    using CommandFramework.API.Models;
    using Impostor.Api.Events.Player;
    using Microsoft.Extensions.Logging;
    using Microsoft.VisualBasic.CompilerServices;
    using static CommandFramework.API.CommandHandler.CommandHandlerUtil;

    /// <summary>
    ///     Command Handler, holds some command settings that will apply to every command.
    /// </summary>
    public class CommandHandler : HandlerOptions, ICommandHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CommandHandler"/> class.
        /// </summary>
        /// <param name="options"> The needed options to set the command handler.</param>
        public CommandHandler(ICommandHandlerOptions options)
        {
            this.Set(options);
            this.Modules = new List<Command>();
            this.IncompleteArguments = new List<IInvalidArgument>();
        }

        // Module loaders.

        /// <summary>
        ///     Gets the command modules.
        /// </summary>
        /// <remarks>
        ///     This holds any modules loaded through <see cref="LoadModules(Command[])"/> or <see cref="LoadModulesFromDLL(string)"/>.
        /// </remarks>
        public List<Command> Modules { get; private set; }

        /// <summary>
        ///     Gets a list of the incomplete arguments.
        /// </summary>
        public List<IInvalidArgument> IncompleteArguments { get; private set; }

        /// <inheritdoc/>
        public void LoadModulesFromDLL(string filePath)
        {
            var assembly = Assembly.LoadFrom(filePath);

            foreach (var type in assembly.GetTypes())
            {
                if (type.BaseType.Name != nameof(Command))
                {
                    continue;
                }

                var possibleAttributes = type.GetCustomAttributes().Where((Attribute t) => t.GetType().Name == nameof(API.Attributes.CommandAttribute));

                if (possibleAttributes.Count() > 0)
                {
                    this.Logger.LogInformation($"{type.Name} was loaded.");
                }
                else
                {
                    this.Logger.LogInformation($"{type.Name} does not have a command attribute and wasn't loaded.");
                    continue;
                }

                var ctors = type.GetConstructors();

                if (ctors.Length <= 0)
                {
                    continue;
                }

                Command command;

                try
                {
                    command = (Command)Activator.CreateInstance(type: type, args: new[] { this });
                    this.Modules.Add(command);
                }
                catch
                {
                    this.Logger.LogInformation("Invalid DLL, skipping over.");
                }
            }
        }

        /// <inheritdoc/>
        public void LoadModule(Command command) => this.Modules.Add(command);

        /// <inheritdoc/>
        public void LoadModules(Command[] commands) => this.Modules.AddRange(commands);

        // Data conversions.

        /// <inheritdoc/>
        public (string Alias, bool CaseSensitive)[] GetCombinedAliases(Command command)
        {
            return command.Aliases == null
                ? new[]
                {
                    (command.MainCommand, command.CaseSensitive
                        ?? this.CaseSensitive
                        ?? CommandHandlerUtil.DefaultHandler.CaseSensitive
                        ?? false),
                }
                : command.Aliases.Concat(new[]
                {
                    (command.MainCommand, command.CaseSensitive
                        ?? this.CaseSensitive
                        ?? CommandHandlerUtil.DefaultHandler.CaseSensitive
                        ?? false),
                }).ToArray();
        }

        /// <inheritdoc/>
        public string[] SplitMessage(Command associatedCommand, string message) => message.Split(associatedCommand.SplitAt ?? this.SplitAt ?? DefaultHandler.SplitAt);

        /// <inheritdoc/>
        public (string Alias, bool CaseSensitive)[] GetDuplicatedAliases()
        {
            var aliases = new List<(string Alias, bool CaseSensitive)>();

            this.Modules.ForEach((Command command) => aliases.AddRange(this.GetCombinedAliases(command).ToHashSet()));

            return aliases.GroupBy(x => x)
               .Where(g => g.Count() > 1)
               .Select(y => y.Key)
               .ToArray();
        }

        // Class events.

        /// <inheritdoc/>
        public virtual void Before(string message)
        {
        }

        /// <inheritdoc/>
        public virtual void After(string message)
        {
        }

        /// <inheritdoc/>
        public void Run(IPlayerChatEvent eventInfo)
        {
            this.Before(eventInfo.Message);

            if (string.IsNullOrWhiteSpace(eventInfo.Message) || this.Modules.Count <= 0)
            {
                return;
            }

            var associatedCommand = this.FindCommand(eventInfo);

            var matchingIncompleteCommands = this.IncompleteArguments.Where((inArg) => inArg.ClientID == eventInfo.ClientPlayer.Client.Id);

            if (associatedCommand == null && matchingIncompleteCommands.Count() > 0)
            {
                var incompleObj = this.IncompleteArguments[0];
                var phraseArg = incompleObj.PhraseArgs.Current;

                if (!this.TryMatchAutoType(eventInfo.Message, phraseArg.AutoType, out object obj) && !this.TryMatchManualType(eventInfo.Message, phraseArg, out obj))
                {
                    eventInfo.ClientPlayer.Character.SendChatAsync(phraseArg.Prompt.Retry ?? associatedCommand.PromptOpts.Retry ?? this.PromptOpts.Retry); // Default message?
                    incompleObj.RetryCount++;
                    return;
                }

                if (obj == null)
                {
                    return;
                }

                incompleObj.PhraseArgs.MoveNext();

                incompleObj.SavedObjs.Add(obj);

                if (incompleObj.PhraseArgs == null)
                {
                    associatedCommand.Execute(eventInfo, incompleObj.SavedObjs.ToArray());
                }
                else
                {
                    eventInfo.ClientPlayer.Character.SendChatAsync(incompleObj.PhraseArgs.Current.Prompt.Start ?? associatedCommand.PromptOpts.Start ?? this.PromptOpts.Start); // Give a default later.
                }

                return;
            }

            if (associatedCommand == null)
            {
                return;
            }

            associatedCommand.Before(eventInfo);
            string[] splitCommand = this.SplitMessage(associatedCommand, eventInfo.Message);

            if (splitCommand.Length <= 0)
            {
                return;
            }

            var phrases = splitCommand.AsEnumerable().GetEnumerator();
            var commandArgsEnumerable = associatedCommand.GetArgs(eventInfo);

            if (commandArgsEnumerable == null)
            {
                associatedCommand.Execute(eventInfo, splitCommand);
                return;
            }

            var commandArgs = commandArgsEnumerable.GetEnumerator();

            List<object> objArray = new List<object>();
            phrases.MoveNext();
            while (phrases.MoveNext() is bool phraseAlive & commandArgs.MoveNext() is bool argsAlive && phraseAlive && argsAlive)
            {
                if (!this.TryMatchAutoType(phrases.Current, commandArgs.Current.AutoType, out object obj) && !this.TryMatchManualType(phrases.Current, commandArgs.Current, out obj))
                {
                    eventInfo.ClientPlayer.Character.SendChatAsync(commandArgs.Current.Prompt.Retry ?? associatedCommand.PromptOpts.Retry ?? this.PromptOpts.Retry);

                    this.IncompleteArguments.Add(new InvalidArgument(eventInfo.ClientPlayer.Client.Id, associatedCommand, objArray, commandArgs, 0));
                    continue;
                }

                if (obj == null)
                {
                    return;
                }

                objArray.Add(obj);
            }

            var test = phrases;

            var test1 = test == null;

            if (phrases.Current == null && commandArgs.Current == null)
            {
                associatedCommand.Execute(eventInfo, objArray.ToArray());
            }
            else if (phrases.Current == null)
            {
                eventInfo.ClientPlayer.Character.SendChatAsync(commandArgs.Current.Prompt.Start ?? associatedCommand.PromptOpts.Start ?? this.PromptOpts.Start); // Give a default later.
            }
            else
            {
                eventInfo.ClientPlayer.Character.SendChatAsync("Too many given arguments..."); // Give a default later.
            }

            // Check if there was too many arguments for the command or too little.
        }

        private void RunRetryMessage(string retry)
        {
            throw new NotImplementedException();
        }

        public bool TryMatchManualType(string phrase, ArgumentGenerator argument, out object value)
        {
            value = null;
            if (argument.ManualType == null)
                return false;
            return argument?.ManualType(phrase, out value) ?? false;
        }

        public bool TryMatchAutoType(string phrase, AutoTypes? autoType, out object value)
        {
            if (string.IsNullOrEmpty(phrase))
            {
                value = null;
                return false;
            }

            value = null;
            bool successfulMatch = true;

            foreach (Enum flag in Enum.GetValues(typeof(AutoTypes)))
            {
                if (autoType.HasValue && autoType.Value.HasFlag(flag))
                {
                    switch (flag)
                    {
                        case AutoTypes.Integer:
                            int tempInt = 0;
                            successfulMatch = successfulMatch && int.TryParse(phrase, out tempInt);
                            value = tempInt;
                            break;

                        case AutoTypes.LongInteger:
                            long tempLong = 0;
                            successfulMatch = successfulMatch && long.TryParse(phrase, out tempLong);
                            value = tempLong;
                            break;

                        case AutoTypes.String:
                            value = phrase;
                            break;

                        default:
                            successfulMatch = false;
                            break;
                    }
                }
            }

            return successfulMatch; // Will overwrite other values with last value.
        }

        // Command handling here.

        /// <inheritdoc/>
        public Command FindCommand(IPlayerChatEvent playerChatEvent, params (string Alias, bool CaseSensitive)[] exludedAliases)
        {
            if (this.Modules.Count <= 0)
            {
                return null;
            }

            foreach (Command command in this.Modules)
            {
                var message = this.SplitMessage(command, playerChatEvent.Message);

                if (message.Length <= 0)
                {
                    continue;
                }

                var prefix = (GetPrefixCommonDenom(command, playerChatEvent)
                    ?? GetPrefixCommonDenom(this, playerChatEvent)
                    ?? GetPrefixCommonDenom(DefaultHandler, playerChatEvent))
                    .Where(ValidPrefix).ToHashSet();

                if (prefix.Count <= 0)
                {
                    continue;
                }

                prefix = new HashSet<string>(prefix.Where((string str) => message[0].StartsWith(str)));

                if (prefix.Count <= 0)
                {
                    continue;
                }

                List<(string Alias, bool CaseSensitive)> aliases = this.GetCombinedAliases(command).ToList();

                if (aliases.Count <= 0)
                {
                    continue;
                }

                List<(string Alias, bool CaseSensitive)> combined = new List<(string Alias, bool CaseSensitive)>();

                foreach (var p in prefix)
                {
                    foreach (var (alias, caseSens) in aliases)
                    {
                        if (exludedAliases.All(((string, bool) excluded) => excluded != (alias, caseSens)))
                        {
                            combined.Add((p + alias, caseSens));
                        }
                        else
                        {
                            this.Logger.LogError($"The {(caseSens ? string.Empty : "non")} case sensitive Command alias {p + alias} has been disabled due to conflicts with other commands.");
                        }
                    }
                }

                if (combined.Any((tupl) => (tupl.CaseSensitive && message[0] == tupl.Alias) || message[0].ToLower() == tupl.Alias.ToLower()))
                {
                    return command;
                }
            }

            return null;
        }
    }
}
