#region Licence

/*
 * Copyright � 2002-2007 the original author or authors.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *      http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#endregion

#region Imports

using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Common.Logging;
using NHibernate;
using Spring.Threading;
using Spring.Transaction.Support;
using Spring.Util;
using System.Data;
using Spring.ConversationWA;
using System.Collections.Generic;

#endregion

namespace Spring.Data.NHibernate.Support
{
    ///<summary>
    ///Based on <see cref="Spring.Data.NHibernate.Support.SessionScope"/> 
    /// for dupport to 'session-per-conversation' pattern.
    ///</summary>
    ///<author>Hailton de Castro</author>
    public class SessionPerConversationScope : IDisposable
    {
        #region Fields

        /// <summary>
        /// The logging instance.
        /// </summary>        
        protected readonly ILog log = LogManager.GetLogger(MethodInfo.GetCurrentMethod().DeclaringType);

        private readonly SessionPerConversationScopeSettings settings;

        // Keys into LogicalThreadContext for runtime values.
        private readonly string ISOPEN_KEY;
        private readonly string OPENER_CONVERSATION_ID_KEY;

        #endregion

        #region Constructor (s)

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionPerConversationScopeSettings"/> class.
        /// Uses default values for <see cref="SessionPerConversationScopeSettings"/> 
        /// </summary>
        public SessionPerConversationScope()
            : this(new SessionPerConversationScopeSettings())
        {
            // noop
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionPerConversationScopeSettings"/> class.
        /// </summary>
        /// <param name="entityInterceptor">Specify the <see cref="IInterceptor"/> to be set on each session provided by this <see cref="SessionPerConversationScope"/> instance.</param>
        /// <param name="defaultFlushMode">Specify the flushmode to be applied on each session provided by this <see cref="SessionPerConversationScope"/> instance.
        /// </param>
        public SessionPerConversationScope(IInterceptor entityInterceptor, FlushMode defaultFlushMode)
            : this(new SessionPerConversationScopeSettings(entityInterceptor, defaultFlushMode))
        {
            // noop
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionScope"/> class.
        /// </summary>
        /// <param name="settings">An <see cref="SessionPerConversationScopeSettings"/> instance holding the scope configuration</param>
        public SessionPerConversationScope(SessionPerConversationScopeSettings settings)
        {
            log = LogManager.GetLogger(this.GetType());
            this.settings = settings;

            ISOPEN_KEY = UniqueKey.GetInstanceScopedString(this, "IsOpen");
            OPENER_CONVERSATION_ID_KEY = UniqueKey.GetInstanceScopedString(this, "OpenerConversationId");
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the flushmode to be applied on each newly created session.
        /// </summary>
        /// <remarks>
        /// This property defaults to <see cref="FlushMode.Never"/> to ensure that modifying objects outside the boundaries 
        /// of a transaction will not be persisted. It is recommended to not change this value but wrap any modifying operation
        /// within a transaction.
        /// </remarks>
        public FlushMode DefaultFlushMode
        {
            get { return settings.DefaultFlushMode; }
        }

        /// <summary>
        /// Get or set the configured EntityInterceptor
        /// </summary>
        public IInterceptor EntityInterceptor
        {
            get
            {
                return settings.EntityInterceptor;
            }
        }

        /// <summary>
        /// Id for conversation that open the Session.
        /// </summary>
        public String OpenerConversationId
        {
            get
            {
                return (String)LogicalThreadContext.GetData(OPENER_CONVERSATION_ID_KEY);
            }
            set
            {
                LogicalThreadContext.SetData(OPENER_CONVERSATION_ID_KEY, value);
            }
        }

        /// <summary>
        /// Gets a flag, whether this scope is in "open" state on the current logical thread.
        /// </summary>
        public bool IsOpen
        {
            get
            {
                return (null != LogicalThreadContext.GetData(ISOPEN_KEY));
            }
        }

        /// <summary>
        /// Sets a flag, whether this scope is in "open" state on the current logical thread.
        /// </summary>
        /// <param name="isOpen"></param>
        private void SetOpen(bool isOpen)
        {
            if (isOpen)
            {
                LogicalThreadContext.SetData(ISOPEN_KEY, ISOPEN_KEY);
            }
            else
            {
                LogicalThreadContext.FreeNamedDataSlot(ISOPEN_KEY);
            }
        }

        #endregion

        #region IDisposable Members

        /// <summary>
        /// NOOP.
        /// </summary>
        public virtual void Dispose()
        {
            //no OP
            log.Warn("I'm not doing anything");
        }

        #endregion

        #region Methods

        /// <summary>
        /// Open a new session or reconect the
        /// <see cref="IConversationState.RootSessionPerConversation"/> in <paramref name="activeConversation"/>.
        /// Participates in an existing session registed with spring's <see cref="TransactionSynchronizationManager"/>
        /// is not alowed.
        /// </summary>
        /// <param name="activeConversation"></param>
        /// <param name="allManagedConversation"></param>
        /// <exception cref="InvalidOperationException">
        /// <list type="bullet">
        /// <item>If there is another conversation with a ISession with opened 
        /// IDbConnection.</item>
        /// <item>If trie to participating in existing Hibernate SessionFactory 
        /// managed by <see cref="TransactionSynchronizationManager"/>.
        /// </item>
        /// </list>
        /// </exception>
        public void Open(IConversationState activeConversation, ICollection<IConversationState> allManagedConversation)
        {
            bool isDebugEnabled = log.IsDebugEnabled;

            if (IsOpen)
            {
                if (activeConversation.Id != this.OpenerConversationId)
                {
                    throw new InvalidOperationException("There is another conversation with a ISession with opened IDbConnection.");
                }
                else
                {
                    if (isDebugEnabled) log.Debug(String.Format("SessionPerConversationScope is already open for this conversation: Id:'{0}'.", activeConversation.Id));
                }
            }
            else
            {
                if (activeConversation.SessionFactory != null)
                {
                    if (isDebugEnabled) log.Debug(String.Format("activeConversation with 'session-per-conversation': Id:'{0}'.", activeConversation.Id));

                    // single session mode
                    if (TransactionSynchronizationManager.HasResource(activeConversation.SessionFactory))
                    {
                        // Do not modify the Session: just set the participate flag.
                        if (isDebugEnabled) log.Debug("Participating in existing Hibernate SessionFactory IS NOT ALOWED.");
                        throw new InvalidOperationException("Participating in existing Hibernate SessionFactory IS NOT ALOWED.");
                    }
                    else
                    {
                        if (isDebugEnabled) log.Debug("Opening single Hibernate Session in SessionPerConversationScope");
                        TransactionSynchronizationManager.BindResource(activeConversation.SessionFactory, new LazySessionPerConversationHolder(this, activeConversation, allManagedConversation));

                        SetOpen(true);
                        this.OpenerConversationId = activeConversation.Id;
                    }
                }
                else
                {
                    if (isDebugEnabled) log.Debug(String.Format("activeConversation with NO 'session-per-conversation': Id:'{0}'.", activeConversation.Id));
                }
            }
        }

        /// <summary>
        /// Close the current view's session and unregisters 
        /// from spring's <see cref="TransactionSynchronizationManager"/>.
        /// </summary>
        /// <param name="sessionFactory">The session factory that IConversationState on <paramref name="allManagedConversation"/> use</param>
        /// <param name="allManagedConversation">A list of conversations which the session can be closed or disconnected</param>
        /// <exception cref="InvalidOperationException">
        /// <list type="bullet">
        /// <item>If start/resume a conversation from a
        /// IConversationManager when exists a diferent IConversationManager 
        /// with open ISession registered on TransactionSynchronizationManager
        /// </item>
        /// <item>If the holder on TransactionSynchronizationManager, is not a LazySessionPerConversationHolder.</item>
        /// </list>
        /// </exception>
        public void Close(ISessionFactory sessionFactory, ICollection<IConversationState> allManagedConversation)
        {
            bool isDebugEnabled = log.IsDebugEnabled;
            if (isDebugEnabled) log.Debug("Trying to close SessionPerConversationScope");

            if (IsOpen)
            {
                try
                {
                    DoClose(sessionFactory, allManagedConversation, isDebugEnabled);
                }
                finally
                {
                    SetOpen(false);
                    this.OpenerConversationId = null;
                }
            }
            else
            {
                if (isDebugEnabled) log.Debug("No open conversation - doing nothing");
            }
        }

        private void DoClose(ISessionFactory sessionFactory, ICollection<IConversationState> allManagedConversation, bool isLogDebugEnabled)
        {
            // single session mode
            if (isLogDebugEnabled) log.Debug("DoClose: Closing SessionPerConversationScope");
            Object holderObj = TransactionSynchronizationManager.UnbindResource(sessionFactory);
            if (holderObj != null)
            {
                if (holderObj is LazySessionPerConversationHolder)
                {
                    LazySessionPerConversationHolder holder = (LazySessionPerConversationHolder)holderObj;
                    if (holder.Owner == this)
                    {
                        holder.CloseAll();
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Can not close session beacause 'holder owner' is not 'this'." +
                            " You are trying to start/resume a conversation from a" +
                            " IConversationManager when exists a diferent IConversationManager " +
                            " with open ISession registered on TransactionSynchronizationManager.");
                    }
                }
                else
                {
                    throw new InvalidOperationException("Can not close session beacause holder, on TransactionSynchronizationManager, is not a LazySessionPerConversationHolder.");
                }
            }
            else
            {
                if (isLogDebugEnabled) log.Warn("DoClose: TransactionSynchronizationManager.UnbindResource(sessionFactory) has no SessionHolder. Should I throw error?");
            }
        }

        private void DoOpenSession(IConversationState conversation)
        {
            lock (this)
            {
                ISession session = null;
                if (conversation.RootSessionPerConversation == null)
                {
                    //new session
                    session = (
                                   (EntityInterceptor != null)
                                   ? conversation.SessionFactory.OpenSession(EntityInterceptor)
                                   : conversation.SessionFactory.OpenSession()
                               );
                    conversation.RootSessionPerConversation = session;
                }
                else
                {
                    //reconnect existing one.
                    if (conversation.DbProvider != null)
                    {
                        if (log.IsDebugEnabled) log.Debug(String.Format("DoOpenSession: Conversation has a DbProvider: Id='{0}'", conversation.Id));
                        if (!conversation.RootSessionPerConversation.IsConnected)
                        {
                            if (log.IsDebugEnabled) log.Debug(String.Format("DoOpenSession: Conversation is not Connected: Id='{0}'", conversation.Id));

                            IDbConnection connection = conversation.DbProvider.CreateConnection();
                            connection.Open();

                            conversation.RootSessionPerConversation.Reconnect(connection);
                        }
                        else
                        {
                            if (log.IsDebugEnabled) log.Debug(String.Format("DoOpenSession: Conversation is already Connected: Id='{0}'", conversation.Id));
                        }
                    }
                    else
                    {
                        if (log.IsDebugEnabled) log.Debug(String.Format("DoOpenSession: Conversation has NO DbProvider: Id='{0}'", conversation.Id));
                        conversation.RootSessionPerConversation.Reconnect();
                    }
                    session = conversation.RootSessionPerConversation;
                }
                session.FlushMode = DefaultFlushMode;
            }
        }

        #endregion

        #region LazySessionPerConversationHolder utility class


        /// <summary>
        /// This sessionHolder creates a session for the active conversation only if it is 
        /// needed (<see cref="Spring.ConversationWA.IConversationState.StartResumeConversation"/>.
        /// </summary>
        /// <remarks>
        /// Although a NHibernateSession deferes creation of db-connections until they are really
        /// needed, instantiation a session is imho still more expensive than this LazySessionHolder. (EE)
        /// </remarks>
        private class LazySessionPerConversationHolder : SessionHolder
        {
            private readonly ILog log = LogManager.GetLogger(typeof(LazySessionPerConversationHolder));
            private SessionPerConversationScope owner;
            public SessionPerConversationScope Owner
            {
                get { return owner; }
            }
            IConversationState activeConversation;
            ICollection<IConversationState> allManagedConversation;

            /// <summary>
            /// Initialize a new instance.
            /// </summary>
            public LazySessionPerConversationHolder(SessionPerConversationScope owner, IConversationState activeConversation, ICollection<IConversationState> allManagedConversation)
            {
                if (log.IsDebugEnabled) log.Debug("Created LazyReconnectableSessionHolder");
                this.owner = owner;
                this.activeConversation = activeConversation;
                this.allManagedConversation = allManagedConversation;
            }

            /// <summary>
            /// Create a new session on demand
            /// </summary>
            protected override void EnsureInitialized()
            {
                if (this.activeConversation.RootSessionPerConversation == null || !this.activeConversation.RootSessionPerConversation.IsConnected)
                {
                    if (log.IsDebugEnabled) log.Debug("EnsureInitialized: 'session-per-conversation' instance requested - opening new session");
                    owner.DoOpenSession(this.activeConversation);
                    AddSession(this.activeConversation.RootSessionPerConversation);
                }
            }

            public void CloseAll()
            {
                foreach (IConversationState conversation in this.allManagedConversation)
                {
                    this.CloseConversation(conversation);
                }
                owner = null;
                this.activeConversation = null;
                this.allManagedConversation = null;

                if (log.IsDebugEnabled) log.Debug("CloseAll LazySessionPerConversationHolder");
            }

            private void CloseConversation(IConversationState conversation)
            {
                if (log.IsDebugEnabled) log.Debug(String.Format("CloseConversation: Id='{0}'", conversation.Id));
                if (conversation.RootSessionPerConversation != null)
                {
                    ISession tmpSession = conversation.RootSessionPerConversation;
                    if (conversation.Ended)
                    {
                        SessionFactoryUtils.CloseSession(tmpSession);
                        conversation.RootSessionPerConversation = null;
                    }
                    else
                    {
                        if (tmpSession.IsConnected)
                            tmpSession.Disconnect();
                    }
                    RemoveSession(tmpSession);
                }
                if (log.IsDebugEnabled) log.Debug("Closed LazySessionPerConversationHolder");
            }
        }

        #endregion
    }
}