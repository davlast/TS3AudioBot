﻿namespace TS3Client
{
	using Messages;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Text.RegularExpressions;

	public abstract class TS3BaseClient : IDisposable
	{
		protected readonly object LockObj = new object();
		private bool eventLoopRunning;
		protected TS3ClientStatus Status;
		protected IEventDispatcher EventDispatcher;

		// EVENTS
		public event EventHandler<TextMessage> OnTextMessageReceived;
		public event EventHandler<ClientEnterView> OnClientEnterView;
		public event EventHandler<ClientLeftView> OnClientLeftView;


		public bool IsConnected => Status == TS3ClientStatus.Connected;
		public ConnectionData CurrentConnectionData { get; private set; }

		protected TS3BaseClient(EventDispatchType dispatcher)
		{
			Status = TS3ClientStatus.Disconnected;
			eventLoopRunning = false;

			switch (dispatcher)
			{
				case EventDispatchType.None: EventDispatcher = new NoEventDispatcher(); break;
				case EventDispatchType.CurrentThread: EventDispatcher = new CurrentThreadEventDisptcher(); break;
				case EventDispatchType.DoubleThread: EventDispatcher = new DoubleThreadEventDispatcher(); break;
				case EventDispatchType.AutoThreadPooled: throw new NotSupportedException(); //break;
				case EventDispatchType.NewThreadEach: throw new NotSupportedException(); //break;
				default: throw new NotSupportedException();
			}
		}

		public void Connect(ConnectionData conData)
		{
			if (string.IsNullOrWhiteSpace(conData.Hostname)) throw new ArgumentNullException(nameof(conData.Hostname));
			if (conData.Port <= 0) throw new ArgumentOutOfRangeException(nameof(conData.Port));

			if (IsConnected)
				Disconnect();

			lock (LockObj)
			{
				Status = TS3ClientStatus.Connecting;
				CurrentConnectionData = conData;
				ConnectInternal(conData);

				EventDispatcher.Init(NetworkLoop);
				Status = TS3ClientStatus.Connected;
			}
		}
		protected abstract void ConnectInternal(ConnectionData conData);
		public void Disconnect()
		{
			lock (LockObj)
			{
				Status = TS3ClientStatus.Quitting;
				OnTextMessageReceived = null;
				OnClientEnterView = null;
				OnClientLeftView = null;
				DisconnectInternal();
			}
		}
		protected abstract void DisconnectInternal();


		#region NETWORK RECEIVE AND DESERIALIZE

		/// <summary>Use this method to start the event dispatcher.
		/// Please keep in mind that this call might be blocking or non-blocking depending on the dispatch-method.
		/// <see cref="EventDispatchType.CurrentThread"/> and <see cref="EventDispatchType.DoubleThread"/> will enter a loop and block the calling thread.
		/// Any other method will start special subroutines and return to the caller.</summary>
		public void EnterEventLoop()
		{
			if (!eventLoopRunning)
			{
				eventLoopRunning = true;
				EventDispatcher.EnterEventLoop();
			}
			else throw new InvalidOperationException("EventLoop can only be run once until disposed.");
		}

		protected abstract void NetworkLoop();

		protected void InvokeEvent(INotification notification)
		{
			// TODO rework
			switch (notification.NotifyType)
			{
				case NotificationType.ChannelCreated: break;
				case NotificationType.ChannelDeleted: break;
				case NotificationType.ChannelChanged: break;
				case NotificationType.ChannelEdited: break;
				case NotificationType.ChannelMoved: break;
				case NotificationType.ChannelPasswordChanged: break;
				case NotificationType.ClientEnterView: EventDispatcher.Invoke(() => OnClientEnterView?.Invoke(this, (ClientEnterView)notification)); break;
				case NotificationType.ClientLeftView: EventDispatcher.Invoke(() => OnClientLeftView?.Invoke(this, (ClientLeftView)notification)); break;
				case NotificationType.ClientMoved: break;
				case NotificationType.ServerEdited: break;
				case NotificationType.TextMessage: EventDispatcher.Invoke(() => OnTextMessageReceived?.Invoke(this, (TextMessage)notification)); break;
				case NotificationType.TokenUsed: break;
				default: throw new InvalidOperationException();
			}
		}

		#endregion

		#region NETWORK SEND

		private static readonly CommandParameter[] NoParameter = new CommandParameter[0];
		private static readonly CommandOption[] NoOptions = new CommandOption[0];
		private static readonly Regex CommandMatch = new Regex(@"[a-z0-9_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ECMAScript);

		[DebuggerStepThrough]
		public IEnumerable<ResponseDictionary> Send(string command)
			=> Send(command, NoParameter);

		[DebuggerStepThrough]
		public IEnumerable<ResponseDictionary> Send(string command, params CommandParameter[] parameter)
			=> Send(command, parameter, NoOptions);

		[DebuggerStepThrough]
		public IEnumerable<ResponseDictionary> Send(string command, CommandParameter[] parameter, CommandOption options)
			=> Send(command, parameter, new[] { options });

		[DebuggerStepThrough]
		public IEnumerable<ResponseDictionary> Send(string command, CommandParameter[] parameter, params CommandOption[] options)
			=> SendInternal(command, parameter, options, null).Cast<ResponseDictionary>();

		[DebuggerStepThrough]
		public IEnumerable<T> Send<T>(string command) where T : IResponse
			=> Send<T>(command, NoParameter);

		[DebuggerStepThrough]
		public IEnumerable<T> Send<T>(string command, params CommandParameter[] parameter) where T : IResponse
			=> Send<T>(command, parameter, NoOptions);

		[DebuggerStepThrough]
		public IEnumerable<T> Send<T>(string command, CommandParameter[] parameter, CommandOption options) where T : IResponse
			=> Send<T>(command, parameter, new[] { options });

		[DebuggerStepThrough]
		public IEnumerable<T> Send<T>(string command, CommandParameter[] parameter, params CommandOption[] options) where T : IResponse
			=> SendInternal(command, parameter, options, typeof(T)).Cast<T>();

		private IEnumerable<IResponse> SendInternal(string command, CommandParameter[] parameter, CommandOption[] options, Type targetType)
		{
			if (string.IsNullOrWhiteSpace(command))
				throw new ArgumentNullException(nameof(command));
			if (!CommandMatch.IsMatch(command))
				throw new ArgumentException("Invalid command characters", nameof(command));

			StringBuilder strb = new StringBuilder(TS3String.Escape(command));

			foreach (var param in parameter)
				strb.Append(' ').Append(param.QueryString);

			foreach (var option in options)
				strb.Append(option.Value);

			string finalCommand = strb.ToString();
			return SendCommand(finalCommand, targetType);
		}

		protected abstract IEnumerable<IResponse> SendCommand(string data, Type targetType);

		#endregion

		#region UNIVERSAL COMMANDS

		public void RegisterNotification(MessageTarget target, int channel) => RegisterNotification(target.GetQueryString(), channel);
		public void RegisterNotification(RequestTarget target, int channel) => RegisterNotification(target.GetQueryString(), channel);
		private void RegisterNotification(string target, int channel)
		{
			var ev = new CommandParameter("event", target.ToLowerInvariant());
			if (target == "channel")
				Send("servernotifyregister", ev, new CommandParameter("id", channel));
			else
				Send("servernotifyregister", ev);
		}


		public void ChangeName(string newName)
			=> Send("clientupdate",
			new CommandParameter("client_nickname", newName));
		public void ChangeDescription(string newDescription, ClientData client)
			=> Send("clientdbedit",
			new CommandParameter("cldbid", client.DatabaseId),
			new CommandParameter("client_description", newDescription));
		public WhoAmI WhoAmI() // Q ?
			=> Send<WhoAmI>("whoami").FirstOrDefault();
		public void SendMessage(string message, ClientData client)
			=> SendMessage(MessageTarget.Private, client.ClientId, message);
		public void SendMessage(string message, ChannelData channel)
			=> SendMessage(MessageTarget.Channel, channel.Id, message);
		public void SendMessage(string message, ServerData server)
			=> SendMessage(MessageTarget.Server, server.VirtualServerId, message);
		public void SendMessage(MessageTarget target, int id, string message)
			=> Send("sendtextmessage",
			new CommandParameter("targetmode", (int)target),
			new CommandParameter("target", id),
			new CommandParameter("msg", message));
		public void SendGlobalMessage(string message)
			=> Send("gm",
			new CommandParameter("msg", message));
		public void KickClientFromServer(ushort[] clientIds)
			=> KickClient(clientIds, RequestTarget.Server);
		public void KickClientFromChannel(ushort[] clientIds)
			=> KickClient(clientIds, RequestTarget.Channel);
		public void KickClient(ushort[] clientIds, RequestTarget target)
			=> Send("clientkick",
			new CommandParameter("reasonid", (int)target),
			CommandBinder.NewBind("clid", clientIds));
		public IEnumerable<ClientData> ClientList()
			=> ClientList(0);
		public IEnumerable<ClientData> ClientList(ClientListOptions options) => Send<ClientData>("clientlist",
			NoParameter, options);
		public IEnumerable<ClientServerGroup> ServerGroupsOfClientDbId(ClientData client)
			=> ServerGroupsOfClientDbId(client.DatabaseId);
		public IEnumerable<ClientServerGroup> ServerGroupsOfClientDbId(ulong clDbId)
			=> Send<ClientServerGroup>("servergroupsbyclientid", new CommandParameter("cldbid", clDbId));
		public ClientDbData ClientDbInfo(ClientData client)
			=> ClientDbInfo(client.DatabaseId);
		public ClientDbData ClientDbInfo(ulong clDbId)
			=> Send<ClientDbData>("clientdbinfo", new CommandParameter("cldbid", clDbId)).FirstOrDefault();

		#endregion

		public void Dispose()
		{
			if (IsConnected)
			{
				Disconnect();

				/*lock (lockObj)
				{
					tcpWriter.Dispose();
					tcpWriter = null;

					tcpReader.Dispose();
					tcpReader = null;

					tcpClient.Close();
					tcpClient = null;
				}*/
			}

			if (EventDispatcher != null)
			{
				EventDispatcher.Dispose();
				EventDispatcher = null;
			}
		}

		protected enum TS3ClientStatus
		{
			Disconnected,
			Connecting,
			Connected,
			Quitting,
		}
	}
}
