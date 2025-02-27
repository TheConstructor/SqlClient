// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Data;
using System.Diagnostics;
using System.Threading;
using Microsoft.Data.Common;

namespace Microsoft.Data.SqlClient
{
    internal enum TransactionState
    {
        Pending = 0,
        Active = 1,
        Aborted = 2,
        Committed = 3,
        Unknown = 4,
    }

    internal enum TransactionType
    {
        LocalFromTSQL = 1,
        LocalFromAPI = 2,
        Delegated = 3,
        Distributed = 4,
        Context = 5,     // only valid in proc.
    }

    sealed internal class SqlInternalTransaction
    {
        internal const long NullTransactionId = 0;

        private TransactionState _transactionState;
        private TransactionType _transactionType;
        private long _transactionId;             // passed in the MARS headers
        private int _openResultCount;           // passed in the MARS headers
        private SqlInternalConnection _innerConnection;
        private bool _disposing;                 // used to prevent us from throwing exceptions while we're disposing
        private WeakReference<SqlTransaction> _parent;                    // weak ref to the outer transaction object; needs to be weak to allow GC to occur.

        private static int _objectTypeCount; // EventSource Counter
        internal readonly int _objectID = System.Threading.Interlocked.Increment(ref _objectTypeCount);

        internal bool RestoreBrokenConnection { get; set; }
        internal bool ConnectionHasBeenRestored { get; set; }

        internal SqlInternalTransaction(SqlInternalConnection innerConnection, TransactionType type, SqlTransaction outerTransaction) : this(innerConnection, type, outerTransaction, NullTransactionId)
        {
        }

        internal SqlInternalTransaction(SqlInternalConnection innerConnection, TransactionType type, SqlTransaction outerTransaction, long transactionId)
        {
            SqlClientEventSource.Log.TryPoolerTraceEvent("<sc.SqlInternalTransaction.ctor|RES|CPOOL> {0}, Created for connection {1}, outer transaction {2}, Type {3}", ObjectID, innerConnection.ObjectID, (null != outerTransaction) ? outerTransaction.ObjectID : -1, (int)type);

            _innerConnection = innerConnection;
            _transactionType = type;

            if (null != outerTransaction)
            {
                _parent = new WeakReference<SqlTransaction>(outerTransaction);
            }

            _transactionId = transactionId;
            RestoreBrokenConnection = false;
            ConnectionHasBeenRestored = false;
        }

        internal bool HasParentTransaction
        {
            get
            {
                // Return true if we are an API started local transaction, or if we were a TSQL
                // started local transaction and were then wrapped with a parent transaction as
                // a result of a later API begin transaction.
                bool result = ((TransactionType.LocalFromAPI == _transactionType) ||
                                (TransactionType.LocalFromTSQL == _transactionType && _parent != null));
                return result;
            }
        }

        internal bool IsAborted
        {
            get
            {
                return (TransactionState.Aborted == _transactionState);
            }
        }

        internal bool IsActive
        {
            get
            {
                return (TransactionState.Active == _transactionState);
            }
        }

        internal bool IsCommitted
        {
            get
            {
                return (TransactionState.Committed == _transactionState);
            }
        }

        internal bool IsCompleted
        {
            get
            {
                return (TransactionState.Aborted == _transactionState
                     || TransactionState.Committed == _transactionState
                     || TransactionState.Unknown == _transactionState);
            }
        }

        internal bool IsContext
        {
            get
            {
                bool result = (TransactionType.Context == _transactionType);
                return result;
            }
        }

        internal bool IsDelegated
        {
            get
            {
                bool result = (TransactionType.Delegated == _transactionType);
                return result;
            }
        }

        internal bool IsDistributed
        {
            get
            {
                bool result = (TransactionType.Distributed == _transactionType);
                return result;
            }
        }

        internal bool IsLocal
        {
            get
            {
                bool result = (TransactionType.LocalFromTSQL == _transactionType
                            || TransactionType.LocalFromAPI == _transactionType
                            || TransactionType.Context == _transactionType);
                return result;
            }
        }

        internal bool IsOrphaned
        {
            get
            {
                // An internal transaction is orphaned when its parent has been
                // reclaimed by GC.
                bool result;
                if (null == _parent)
                {
                    // No parent, so we better be LocalFromTSQL.  Should we even return in this case -
                    // since it could be argued this is invalid?
                    Debug.Fail("Why are we calling IsOrphaned with no parent?");
                    Debug.Assert(_transactionType == TransactionType.LocalFromTSQL, "invalid state");
                    result = false;
                }
                else if (!_parent.TryGetTarget(out SqlTransaction _))
                {
                    // We had a parent, but parent was GC'ed.
                    result = true;
                }
                else
                {
                    // We have a parent, and parent is alive.
                    result = false;
                }

                return result;
            }
        }

        internal bool IsZombied
        {
            get
            {
                return (null == _innerConnection);
            }
        }

        internal int ObjectID
        {
            get
            {
                return _objectID;
            }
        }

        internal int OpenResultsCount
        {
            get
            {
                return _openResultCount;
            }
        }

        internal SqlTransaction Parent
        {
            get
            {
                SqlTransaction result = null;
                // Should we protect against this, since this probably is an invalid state?
                Debug.Assert(null != _parent, "Why are we calling Parent with no parent?");
                if (_parent != null && _parent.TryGetTarget(out SqlTransaction target))
                {
                    result = target;
                }
                return result;
            }
        }

        internal long TransactionId
        {
            get
            {
                return _transactionId;
            }
            set
            {
                Debug.Assert(NullTransactionId == _transactionId, "setting transaction cookie while one is active?");
                _transactionId = value;
            }
        }

        internal void Activate()
        {
            _transactionState = TransactionState.Active;
        }

        private void CheckTransactionLevelAndZombie()
        {
            try
            {
                if (!IsZombied && GetServerTransactionLevel() == 0)
                {
                    // If not zombied, not closed, and not in transaction, zombie.
                    Zombie();
                }
            }
            catch (Exception e)
            {
                // UNDONE - should not be catching all exceptions!!!
                if (!ADP.IsCatchableExceptionType(e))
                {
                    throw;
                }

                ADP.TraceExceptionWithoutRethrow(e);
                Zombie(); // If exception caught when trying to check level, zombie.
            }
        }

        internal void CloseFromConnection()
        {
            SqlInternalConnection innerConnection = _innerConnection;

            Debug.Assert(innerConnection != null, "How can we be here if the connection is null?");
            SqlClientEventSource.Log.TryPoolerTraceEvent("<sc.SqlInteralTransaction.CloseFromConnection|RES|CPOOL> {0}, Closing", ObjectID);

            bool processFinallyBlock = true;
            try
            {
                innerConnection.ExecuteTransaction(SqlInternalConnection.TransactionRequest.IfRollback, null, IsolationLevel.Unspecified, null, false);
            }
            catch (Exception e)
            {
                processFinallyBlock = ADP.IsCatchableExceptionType(e);
                throw;
            }
            finally
            {
                TdsParser.ReliabilitySection.Assert("unreliable call to CloseFromConnection");  // you need to setup for a thread abort somewhere before you call this method
                if (processFinallyBlock)
                {
                    // Always ensure we're zombied; Yukon will send an EnvChange that
                    // will cause the zombie, but only if we actually go to the wire;
                    // Sphinx and Shiloh won't send the env change, so we have to handle
                    // them ourselves.
                    Zombie();
                }
            }
        }

        internal void Commit()
        {
            using (TryEventScope.Create("<sc.SqlInternalTransaction.Commit|API> {0}", ObjectID))
            {
                if (_innerConnection.IsLockedForBulkCopy)
                {
                    throw SQL.ConnectionLockedForBcpEvent();
                }

                _innerConnection.ValidateConnectionForExecute(null);

                // If this transaction has been completed, throw exception since it is unusable.
                try
                {
                    // COMMIT ignores transaction names, and so there is no reason to pass it anything.  COMMIT
                    // simply commits the transaction from the most recent BEGIN, nested or otherwise.
                    _innerConnection.ExecuteTransaction(SqlInternalConnection.TransactionRequest.Commit, null, IsolationLevel.Unspecified, null, false);

                    // SQL BU DT 291159 - perform full Zombie on pre-Yukon, but do not actually
                    // complete internal transaction until informed by server in the case of Yukon
                    // or later.
                    if (!IsZombied && !_innerConnection.IsYukonOrNewer)
                    {
                        // Since nested transactions are no longer allowed, set flag to false.
                        // This transaction has been completed.
                        Zombie();
                    }
                    else
                    {
                        ZombieParent();
                    }
                }
                catch (Exception e)
                {
                    // UNDONE - should not be catching all exceptions!!!
                    if (ADP.IsCatchableExceptionType(e))
                    {
                        CheckTransactionLevelAndZombie();
                    }

                    throw;
                }
            }
        }

        internal void Completed(TransactionState transactionState)
        {
            Debug.Assert(TransactionState.Active < transactionState, "invalid transaction completion state?");
            _transactionState = transactionState;
            Zombie();
        }

        internal Int32 DecrementAndObtainOpenResultCount()
        {
            Int32 openResultCount = Interlocked.Decrement(ref _openResultCount);
            if (openResultCount < 0)
            {
                throw SQL.OpenResultCountExceeded();
            }
            return openResultCount;
        }

        internal void Dispose()
        {
            this.Dispose(true);
            System.GC.SuppressFinalize(this);
        }

        private /*protected override*/ void Dispose(bool disposing)
        {
            SqlClientEventSource.Log.TryPoolerTraceEvent("<sc.SqlInteralTransaction.Dispose|RES|CPOOL> {0}, Disposing", ObjectID);

            if (disposing)
            {
                if (null != _innerConnection)
                {
                    // implicitly rollback if transaction still valid
                    _disposing = true;
                    this.Rollback();
                }
            }
        }

        private int GetServerTransactionLevel()
        {
            // This function is needed for those times when it is impossible to determine the server's
            // transaction level, unless the user's arguments were parsed - which is something we don't want
            // to do.  An example when it is impossible to determine the level is after a rollback.

            // TODO: we really ought to be able to execute without using the public objects...

            using (SqlCommand transactionLevelCommand = new SqlCommand("set @out = @@trancount", (SqlConnection)(_innerConnection.Owner)))
            {
                transactionLevelCommand.Transaction = Parent;

                SqlParameter parameter = new SqlParameter("@out", SqlDbType.Int);
                parameter.Direction = ParameterDirection.Output;
                transactionLevelCommand.Parameters.Add(parameter);

                // UNDONE: use a singleton select here
                // UNDONE: execute without SqlClientPermission.Demand()
                transactionLevelCommand.RunExecuteReader(0, RunBehavior.UntilDone, false /* returnDataStream */, nameof(GetServerTransactionLevel));

                return (int)parameter.Value;
            }
        }

        internal Int32 IncrementAndObtainOpenResultCount()
        {
            Int32 openResultCount = Interlocked.Increment(ref _openResultCount);

            if (openResultCount < 0)
            {
                throw SQL.OpenResultCountExceeded();
            }
            return openResultCount;
        }

        internal void InitParent(SqlTransaction transaction)
        {
            Debug.Assert(_parent == null, "Why do we have a parent on InitParent?");
            _parent = new WeakReference<SqlTransaction>(transaction);
        }

        internal void Rollback()
        {
            using (TryEventScope.Create("<sc.SqlInternalTransaction.Rollback|API> {0}", ObjectID))
            {
                if (_innerConnection.IsLockedForBulkCopy)
                {
                    throw SQL.ConnectionLockedForBcpEvent();
                }

                _innerConnection.ValidateConnectionForExecute(null);

                try
                {
                    // If no arg is given to ROLLBACK it will rollback to the outermost begin - rolling back
                    // all nested transactions as well as the outermost transaction.
                    _innerConnection.ExecuteTransaction(SqlInternalConnection.TransactionRequest.IfRollback, null, IsolationLevel.Unspecified, null, false);

                    // Since Rollback will rollback to outermost begin, no need to check
                    // server transaction level.  This transaction has been completed.
                    Zombie();
                }
                catch (Exception e)
                {
                    // UNDONE - should not be catching all exceptions!!!
                    if (ADP.IsCatchableExceptionType(e))
                    {
                        CheckTransactionLevelAndZombie();

                        if (!_disposing)
                        {
                            throw;
                        }
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }

        internal void Rollback(string transactionName)
        {
            using (TryEventScope.Create("<sc.SqlInternalTransaction.Rollback|API> {0}, transactionName='{1}'", ObjectID, transactionName))
            {
                if (_innerConnection.IsLockedForBulkCopy)
                {
                    throw SQL.ConnectionLockedForBcpEvent();
                }

                _innerConnection.ValidateConnectionForExecute(null);

                // ROLLBACK takes either a save point name or a transaction name.  It will rollback the
                // transaction to either the save point with the save point name or begin with the
                // transaction name.  NOTE: for simplicity it is possible to give all save point names
                // the same name, and ROLLBACK will simply rollback to the most recent save point with the
                // save point name.
                if (ADP.IsEmpty(transactionName))
                {
                    throw SQL.NullEmptyTransactionName();
                }

                try
                {
                    _innerConnection.ExecuteTransaction(SqlInternalConnection.TransactionRequest.Rollback, transactionName, IsolationLevel.Unspecified, null, false);

                    if (!IsZombied && !_innerConnection.IsYukonOrNewer)
                    {
                        // Check if Zombied before making round-trip to server.
                        // Against Yukon we receive an envchange on the ExecuteTransaction above on the
                        // parser that calls back into SqlTransaction for the Zombie() call.
                        CheckTransactionLevelAndZombie();
                    }
                }
                catch (Exception e)
                {
                    // UNDONE - should not be catching all exceptions!!!
                    if (ADP.IsCatchableExceptionType(e))
                    {
                        CheckTransactionLevelAndZombie();
                    }
                    throw;
                }
            }
        }

        internal void Save(string savePointName)
        {
            using (TryEventScope.Create("<sc.SqlInternalTransaction.Save|API> {0}, savePointName='{1}'", ObjectID, savePointName))
            {
                _innerConnection.ValidateConnectionForExecute(null);

                // ROLLBACK takes either a save point name or a transaction name.  It will rollback the
                // transaction to either the save point with the save point name or begin with the
                // transaction name.  So, to rollback a nested transaction you must have a save point.
                // SAVE TRANSACTION MUST HAVE AN ARGUMENT!!!  Save Transaction without an arg throws an
                // exception from the server.  So, an overload for SaveTransaction without an arg doesn't make
                // sense to have.  Save Transaction does not affect the transaction level.
                if (ADP.IsEmpty(savePointName))
                {
                    throw SQL.NullEmptyTransactionName();
                }

                try
                {
                    _innerConnection.ExecuteTransaction(SqlInternalConnection.TransactionRequest.Save, savePointName, IsolationLevel.Unspecified, null, false);
                }
                catch (Exception e)
                {
                    // UNDONE - should not be catching all exceptions!!!
                    if (ADP.IsCatchableExceptionType(e))
                    {
                        CheckTransactionLevelAndZombie();
                    }

                    throw;
                }
            }
        }

        internal void Zombie()
        {
            // Called by several places in the code to ensure that the outer
            // transaction object has been zombied and the parser has broken
            // it's reference to us.

            // NOTE: we'll be called from the TdsParser when it gets appropriate
            // ENVCHANGE events that indicate the transaction has completed, however
            // we cannot rely upon those events occuring in the case of pre-Yukon
            // servers (and when we don't go to the wire because the connection
            // is broken) so we can also be called from the Commit/Rollback/Save
            // methods to handle that case as well.

            // There are two parts to a full zombie:
            // 1) Zombie parent and disconnect outer transaction from internal transaction
            // 2) Disconnect internal transaction from connection and parser
            // Number 1 needs to be done whenever a SqlTransaction object is completed.  Number
            // 2 is only done when a transaction is actually completed.  Since users can begin
            // transactions both in and outside of the API, and since nested begins are not actual
            // transactions we need to distinguish between #1 and #2.  See SQL BU DT 291159
            // for further details.

            ZombieParent();

            SqlInternalConnection innerConnection = _innerConnection;
            _innerConnection = null;

            if (null != innerConnection)
            {
                innerConnection.DisconnectTransaction(this);
            }
        }

        private void ZombieParent()
        {
            if (_parent != null && _parent.TryGetTarget(out SqlTransaction parent))
            {
                parent.Zombie();
            }
            _parent = null;
        }

        internal string TraceString()
        {
            return string.Format(/*IFormatProvider*/ null, "(ObjId={0}, tranId={1}, state={2}, type={3}, open={4}, disp={5}",
                        ObjectID, _transactionId, _transactionState, _transactionType, _openResultCount, _disposing);
        }
    }
}
