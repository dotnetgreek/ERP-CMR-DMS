﻿/********************************************************
 * Module Name    : Workflow
 * Purpose        : 
 * Class Used     : X_AD_WF_Activity
 * Chronological Development
 * Veena Pandey     02-May-2009
 * Lakhwinder       11-Nov-2013
 ******************************************************/

using System;
using System.Collections.Generic;
//using System.Linq;
using System.Text;
using System.Data;
using System.IO;
using VAdvantage.Classes;
using VAdvantage.Model;
using VAdvantage.Process;
using VAdvantage.DataBase;
using VAdvantage.Logging;
using VAdvantage.Utility;
using VAdvantage.ProcessEngine;
using VAdvantage.Print;
using System.Net;
using System.Threading;
using System.Reflection;
namespace VAdvantage.WF
{
    public class MWFActivity : X_AD_WF_Activity
    {
        /**	State Machine				*/
        private StateEngine _state = null;
        /**	Workflow Node				*/
        private MWFNode _node = null;
        /** Transaction					*/
        private Trx _trx = null;
        /**	Audit						*/
        private MWFEventAudit _audit = null;
        /** Persistent Object			*/
        private PO _po = null;
        /** Document Status				*/
        private String _docStatus = null;
        /**	New Value to save in audit	*/
        private String _newValue = null;
        /** Process						*/
        private MWFProcess _process = null;
        /** Post Immediate Candidate	*/
        private DocAction _postImmediate = null;
        /** List of email recipients	*/
        private List<String> _emails = new List<String>();

        /**	Static Logger	*/
        private static VLogger _log = VLogger.GetVLogger(typeof(MWFActivity).FullName);

        /// <summary>
        /// Standard Constructor
        /// </summary>
        /// <param name="ctx">context</param>
        /// <param name="AD_WF_Activity_ID">id</param>
        /// <param name="trxName">transaction</param>
        public MWFActivity(Ctx ctx, int AD_WF_Activity_ID, Trx trxName)
            : base(ctx, AD_WF_Activity_ID, trxName)
        {
            if (AD_WF_Activity_ID == 0)
                throw new ArgumentException("Cannot create new WF Activity directly");
            _state = new StateEngine(GetWFState());
        }

        /// <summary>
        /// Load Constructor
        /// </summary>
        /// <param name="ctx">context</param>
        /// <param name="rs">result set</param>
        /// <param name="trxName">transaction</param>
        public MWFActivity(Ctx ctx, DataRow rs, Trx trxName)
            : base(ctx, rs, trxName)
        {
            _state = new StateEngine(GetWFState());
        }

        /// <summary>
        /// Parent Contructor
        /// </summary>
        /// <param name="process">process</param>
        /// <param name="AD_WF_Node_ID">start node id</param>
        public MWFActivity(MWFProcess process, int AD_WF_Node_ID)
            : base(process.GetCtx(), 0, process.Get_TrxName())
        {
            SetAD_WF_Process_ID(process.GetAD_WF_Process_ID());
            SetPriority(process.GetPriority());
            //	Document Link
            SetAD_Table_ID(process.GetAD_Table_ID());
            SetRecord_ID(process.GetRecord_ID());
            //	Status
            base.SetWFState(WFSTATE_NotStarted);
            _state = new StateEngine(GetWFState());
            SetProcessed(false);
            //	Set Workflow Node
            SetAD_Workflow_ID(process.GetAD_Workflow_ID());
            SetAD_WF_Node_ID(AD_WF_Node_ID);
            //	Node Priority & End Duration
            MWFNode node = MWFNode.Get(GetCtx(), AD_WF_Node_ID);
            int priority = node.GetPriority();
            if (priority != 0 && priority != GetPriority())
                SetPriority(priority);
            long limitMS = node.GetDurationLimitMS();
            if (limitMS != 0)
            {
                //SetEndWaitTime(new DateTime(limitMS + CommonFunctions.CurrentTimeMillis())); // not gives correct output
                SetEndWaitTime(DateTime.Now.AddMilliseconds(limitMS));
            }
            //	Responsible
            SetResponsible(process);
            Save();
            //
            _audit = new MWFEventAudit(this);
            _audit.Save();
            //
            _process = process;
        }


        /// <summary>
        /// Get Activities for table/tecord
        /// </summary>
        /// <param name="ctx">context</param>
        /// <param name="AD_Table_ID">table id</param>
        /// <param name="Record_ID">record id</param>
        /// <param name="activeOnly">if true only not processed records are returned</param>
        /// <returns>activity</returns>
        public static MWFActivity[] Get(Ctx ctx, int AD_Table_ID, int Record_ID, bool activeOnly)
        {
            List<MWFActivity> list = new List<MWFActivity>();
            String sql = "SELECT * FROM AD_WF_Activity WHERE AD_Table_ID=" + AD_Table_ID + " AND Record_ID=" + Record_ID + "";
            if (activeOnly)
                sql += " AND Processed<>'Y'";
            sql += " ORDER BY AD_WF_Activity_ID";
            try
            {
                DataSet ds = DataBase.DB.ExecuteDataset(sql, null, null);
                if (ds.Tables.Count > 0)
                {
                    foreach (DataRow dr in ds.Tables[0].Rows)
                    {
                        list.Add(new MWFActivity(ctx, dr, null));
                    }
                }
            }
            catch (Exception e)
            {
                _log.Log(Level.SEVERE, sql, e);
            }
            MWFActivity[] retValue = new MWFActivity[list.Count];
            retValue = list.ToArray();
            return retValue;
        }

        /// <summary>
        /// Get Active Info
        /// </summary>
        /// <param name="ctx">context</param>
        /// <param name="AD_Table_ID">table id</param>
        /// <param name="Record_ID">record id</param>
        /// <returns>activity summary</returns>
        public static String GetActiveInfo(Ctx ctx, int AD_Table_ID, int Record_ID)
        {
            MWFActivity[] acts = Get(ctx, AD_Table_ID, Record_ID, true);
            if (acts == null || acts.Length == 0)
                return null;
            //
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < acts.Length; i++)
            {
                if (i > 0)
                    sb.Append("\n");
                MWFActivity activity = acts[i];
                sb.Append(activity.ToStringX());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Get State
        /// </summary>
        /// <returns>state</returns>
        public StateEngine GetState()
        {
            return _state;
        }

        /// <summary>
        /// Set Activity State
        /// </summary>
        /// <param name="WFState">WFState</param>
        public new void SetWFState(String WFState)
        {
            if (_state == null)
                _state = new StateEngine(GetWFState());
            if (_state.IsClosed())
                return;
            if (GetWFState().Equals(WFState))
                return;
            //
            if (_state.IsValidNewState(WFState))
            {
                String oldState = GetWFState();
                log.Fine(oldState + "->" + WFState + ", Msg=" + GetTextMsg());
                base.SetWFState(WFState);
                _state = new StateEngine(GetWFState());
                Save();			//	closed in MWFProcess.checkActivities()
                UpdateEventAudit();

                //	Inform Process
                if (_process == null)
                    _process = new MWFProcess(GetCtx(), GetAD_WF_Process_ID(), null);
                _process.CheckActivities();
            }
            else
            {
                String msg = "Ignored Invalid Transformation - New="
                    + WFState + ", Current=" + GetWFState();
                log.Log(Level.SEVERE, msg);
                //Trace.printStack();
                SetTextMsg("Set WFState - " + msg);
                Save();
            }
        }

        /// <summary>
        /// Is Activity closed
        /// </summary>
        /// <returns>true if closed</returns>
        public bool IsClosed()
        {
            return _state.IsClosed();
        }

        /// <summary>
        /// Update Event Audit
        /// </summary>
        private void UpdateEventAudit()
        {
            //	log.fine("");
            GetEventAudit();
            _audit.SetTextMsg(GetTextMsg());
            _audit.SetWFState(GetWFState());
            if (_newValue != null)
                _audit.SetNewValue(_newValue);
            if (_state.IsClosed())
            {
                _audit.SetEventType(MWFEventAudit.EVENTTYPE_ProcessCompleted);
                long ms = CommonFunctions.CurrentTimeMillis();//-_audit.GetCreated().getTime();
                _audit.SetElapsedTimeMS(Convert.ToDecimal(ms));

            }
            else
                _audit.SetEventType(MWFEventAudit.EVENTTYPE_StateChanged);
            _audit.Save();
        }

        /// <summary>
        /// Get/Create Event Audit
        /// </summary>
        /// <returns>event</returns>
        public MWFEventAudit GetEventAudit()
        {
            if (_audit != null)
                return _audit;
            MWFEventAudit[] events = MWFEventAudit.Get(GetCtx(), GetAD_WF_Process_ID(), GetAD_WF_Node_ID());
            if (events == null || events.Length == 0)
                _audit = new MWFEventAudit(this);
            else
                _audit = events[events.Length - 1];		//	last event
            return _audit;
        }

        /// <summary>
        /// Get Persistent Object in Transaction
        /// </summary>
        /// <param name="trxName">transaction</param>
        /// <returns>po</returns>
        //public PO GetPO(String trxName)
        //{
        //    if (_po != null)
        //        return _po;

        //    MTable table = MTable.Get(GetCtx(), GetAD_Table_ID());
        //    _po = table.GetPO(GetCtx(), GetRecord_ID(), trxName);
        //    return _po;
        //}

        /// <summary>
        /// Get Persistent Object in Transaction 
        /// </summary>
        /// <param name="trx">transaction</param>
        /// <returns>po</returns>
        public PO GetPO(Trx trx)
        {

            if (_po != null)
            {
                if ((_po.Get_Trx() == null) && (trx == null))
                    return _po;
                if ((_po.Get_Trx() != null) && (trx != null)
                        && _po.Get_Trx().Equals(trx))
                    return _po;
                log.Fine("Reloading - PO=" + _po.Get_Trx() + " -> " + trx);
                _po.Load(trx);		//	reload
                return _po;
            }

            MTable table = MTable.Get(GetCtx(), GetAD_Table_ID());
            _po = table.GetPO(GetCtx(), GetRecord_ID(), trx);
            return _po;




            //if (_po != null)
            //    return _po;
            //if (trx == null)
            //    return GetPO((String)null);
            //return GetPO(trx);
        }

        /// <summary>
        /// Get Persistent Object.
        /// </summary>
        /// <returns>po</returns>
        public PO GetPO()
        {
            return GetPO(_trx);
        }

        /// <summary>
        /// Get PO AD_Client_ID
        /// </summary>
        /// <param name="trxName">transaction</param>
        /// <returns>client of PO or -1</returns>
        public int GetPO_AD_Client_ID(Trx trxName)
        {
            if (_po == null && trxName == null)
                GetPO(_trx);
            if (_po == null)
                GetPO(trxName);
            if (_po != null)
                return _po.GetAD_Client_ID();
            return -1;
        }

        /// <summary>
        /// Get Attribute Value (based on Node) of PO
        /// </summary>
        /// <returns>Attribute Value or null</returns>
        public Object GetAttributeValue()
        {
            MWFNode node = GetNode();
            if (node == null)
                return null;
            int AD_Column_ID = node.GetAD_Column_ID();
            if (AD_Column_ID == 0)
                return null;
            PO po = GetPO();
            if (po.Get_ID() == 0)
                return null;
            return po.Get_ValueOfColumn(AD_Column_ID);
        }

        /// <summary>
        /// Is SO Trx
        /// </summary>
        /// <returns>SO Trx or of not found true</returns>
        public bool IsSOTrx()
        {
            PO po = GetPO();
            if (po.Get_ID() == 0)
                return true;
            //	Is there a Column?
            int index = po.Get_ColumnIndex("IsSOTrx");
            if (index < 0)
            {
                if (po.Get_TableName().StartsWith("M_"))
                    return false;
                return true;
            }
            //	we have a column
            try
            {
                bool IsSOTrx = (bool)po.Get_Value(index);
                return IsSOTrx;
            }
            catch (Exception e)
            {
                log.Log(Level.SEVERE, "", e);
            }
            return true;
        }

        /// <summary>
        /// Set AD_WF_Node_ID.
        /// (Re)Set to Not Started
        /// </summary>
        /// <param name="AD_WF_Node_ID">new node</param>
        public new  void SetAD_WF_Node_ID(int AD_WF_Node_ID)
        {
            if (AD_WF_Node_ID == 0)
                throw new ArgumentException("Workflow Node is not defined");
            base.SetAD_WF_Node_ID(AD_WF_Node_ID);
            //
            if (!WFSTATE_NotStarted.Equals(GetWFState()))
            {
                base.SetWFState(WFSTATE_NotStarted);
                _state = new StateEngine(GetWFState());
            }
            if (IsProcessed())
                SetProcessed(false);
        }

        /// <summary>
        /// Get WF Node
        /// </summary>
        /// <returns>node</returns>
        public MWFNode GetNode()
        {
            if (_node == null)
                _node = MWFNode.Get(GetCtx(), GetAD_WF_Node_ID());
            return _node;
        }

        /// <summary>
        /// Get WF Node Name
        /// </summary>
        /// <returns>translated node name</returns>
        public String GetNodeName()
        {
            return GetNode().GetName(true);
        }

        /// <summary>
        /// Get Node Description
        /// </summary>
        /// <returns>translated node description</returns>
        public String GetNodeDescription()
        {
            return GetNode().GetDescription(true);
        }

        /// <summary>
        /// Get Node Help
        /// </summary>
        /// <returns>translated node help</returns>
        public String GetNodeHelp()
        {
            return GetNode().GetHelp(true);
        }

        /// <summary>
        /// Is this an user Approval step?
        /// </summary>
        /// <returns>true if User Approval</returns>
        public bool IsUserApproval()
        {
            return GetNode().IsUserApproval();
        }

        /// <summary>
        /// Is this a Manual user step?
        /// </summary>
        /// <returns>true if Window/Form/..</returns>
        public bool IsUserManual()
        {
            return GetNode().IsUserManual();
        }

        /// <summary>
        /// Is this a user choice step?
        /// </summary>
        /// <returns>true if User Choice</returns>
        public bool IsUserChoice()
        {
            return GetNode().IsUserChoice();
        }

        /// <summary>
        /// Set Text Msg (add to existing)
        /// </summary>
        /// <param name="textMsg">msg</param>
        public new void SetTextMsg(String textMsg)
        {
            if (textMsg == null || textMsg.Length == 0)
                return;
            String oldText = GetTextMsg();
            if (oldText == null || oldText.Length == 0)
            {
                //base.SetTextMsg(Utility.trimSize(textMsg, 1000));
                base.SetTextMsg(textMsg);
            }
            else if (textMsg != null && textMsg.Length > 0)
            {
                //base.SetTextMsg(Utility.trimSize(oldText + "\n - " + textMsg, 1000));
                base.SetTextMsg(oldText + "\n - " + textMsg);
            }
        }

        /// <summary>
        /// Add to Text Msg
        /// </summary>
        /// <param name="obj">some object</param>
        public void AddTextMsg(Object obj)
        {
            if (obj == null)
                return;
            //
            StringBuilder textMsg = new StringBuilder(obj.ToString());
            //if (obj instanceof Exception)
            if (obj.GetType() == typeof(Exception))
            {
                Exception ex = (Exception)obj;
                //while (ex != null)
                //{
                //    StackTraceElement[] st = ex.getStackTrace();
                //    for (int i = 0; i < st.Length; i++)
                //    {
                //        StackTraceElement ste = st[i];
                //        if (i == 0 || ste.getClassName().startsWith("org.Vienna"))
                //            TextMsg.append(" (").append(i).append("): ")
                //                .append(ste.toString())
                //                .append("\n");
                //    }
                //    if (ex.getCause() instanceof Exception)
                //        ex = (Exception)ex.getCause();
                //    else
                //        ex = null;
                //}
            }
            //
            String oldText = GetTextMsg();
            if (oldText == null || oldText.Length == 0)
            {
                //base.SetTextMsg(Utility.trimSize(TextMsg.toString(),1000));
                base.SetTextMsg(textMsg.ToString());
            }
            else if (textMsg != null && textMsg.Length > 0)
            {
                //base.SetTextMsg(Utility.trimSize(oldText + "\n - " + textMsg.ToString(),1000));
                base.SetTextMsg(oldText + "\n - " + textMsg.ToString());
            }
        }

        /// <summary>
        /// Get WF State text
        /// </summary>
        /// <returns>state text</returns>
        public String GetWFStateText()
        {
            return MRefList.GetListName(GetCtx(), WFSTATE_AD_Reference_ID, GetWFState());
        }

        /// <summary>
        /// Set Responsible and User from Process / Node
        /// </summary>
        /// <param name="process">process</param>
        private void SetResponsible(MWFProcess process)
        {
            //	Responsible
            int AD_WF_Responsible_ID = GetNode().GetAD_WF_Responsible_ID();
            if (AD_WF_Responsible_ID == 0)	//	not defined on Node Level
                AD_WF_Responsible_ID = process.GetAD_WF_Responsible_ID();
            SetAD_WF_Responsible_ID(AD_WF_Responsible_ID);
            MWFResponsible resp = GetResponsible();

            //	User - Directly responsible
            int AD_User_ID = resp.GetAD_User_ID();
            //	Invoker - get Sales Rep or last updater of document
            if (AD_User_ID == 0 && resp.IsInvoker())
                AD_User_ID = process.GetAD_User_ID();
            //
            SetAD_User_ID(AD_User_ID);
        }

        /// <summary>
        /// Get Responsible
        /// </summary>
        /// <returns>responsible</returns>
        public MWFResponsible GetResponsible()
        {
            MWFResponsible resp = MWFResponsible.Get(GetCtx(), GetAD_WF_Responsible_ID());
            return resp;
        }

        /// <summary>
        /// Is Invoker (no user & no role)
        /// </summary>
        /// <returns>true if invoker</returns>
        public bool IsInvoker()
        {
            return GetResponsible().IsInvoker();
        }

        /// <summary>
        /// Get Approval User.
        /// If the returned user is the same, the document is approved.
        /// </summary>
        /// <param name="AD_User_ID">starting User</param>
        /// <param name="C_Currency_ID">currency</param>
        /// <param name="amount">amount</param>
        /// <param name="AD_Org_ID">document organization</param>
        /// <param name="ownDocument">the document is owned by AD_User_ID</param>
        /// <returns>AD_User_ID - if -1 no Approver</returns>
        public int GetApprovalUser(int AD_User_ID, int C_Currency_ID, Decimal amount,
            int AD_Org_ID, bool ownDocument)
        {
            //	Nothing to approve
            if ( Math.Sign(amount) == 0)
                return AD_User_ID;

            //	Starting user
            MUser user = MUser.Get(GetCtx(), AD_User_ID);
            log.Info("For User=" + user + ", Amt=" + amount + ", Own=" + ownDocument);

            MUser oldUser = null;
            while (user != null)
            {
                if (user.Equals(oldUser))
                {
                    log.Info("Loop - " + user.GetName());
                    return -1;
                }
                oldUser = user;
                log.Fine("User=" + user.GetName());
                //	Get Roles of User
                MRole[] roles = user.GetRoles(AD_Org_ID);
                for (int i = 0; i < roles.Length; i++)
                {
                    MRole role = roles[i];
                    if (ownDocument && !role.IsCanApproveOwnDoc())
                        continue;	//	find a role with allows them to approve own
                    Decimal roleAmt = role.GetAmtApproval();
                    if ( Math.Sign(roleAmt) == 0)
                        continue;
                    if (C_Currency_ID != role.GetC_Currency_ID()
                        && role.GetC_Currency_ID() != 0)			//	No currency = amt only
                    {
                        roleAmt = MConversionRate.Convert(GetCtx(),//	today & default rate 
                            roleAmt, role.GetC_Currency_ID(),
                            C_Currency_ID, GetAD_Client_ID(), AD_Org_ID);
                        if ( Math.Sign(roleAmt) == 0)
                            continue;
                    }
                    bool approved = amount.CompareTo(roleAmt) <= 0;
                    log.Fine("Approved=" + approved + " - User=" + user.GetName() + ", Role=" + role.GetName()
                        + ", ApprovalAmt=" + roleAmt);
                    if (approved)
                        return user.GetAD_User_ID();
                }

                //	**** Find next User 
                //	Get Supervisor
                if (user.GetSupervisor_ID() != 0)
                {
                    user = MUser.Get(GetCtx(), user.GetSupervisor_ID());
                    log.Fine("Supervisor: " + user.GetName());
                }
                else
                {
                    log.Fine("No Supervisor");
                    MOrg org = MOrg.Get(GetCtx(), AD_Org_ID);
                    MOrgInfo orgInfo = org.GetInfo();
                    //	Get Org Supervisor
                    if (orgInfo.GetSupervisor_ID() != 0)
                    {
                        user = MUser.Get(GetCtx(), orgInfo.GetSupervisor_ID());
                        log.Fine("Org=" + org.GetName() + ",Supervisor: " + user.GetName());
                    }
                    else
                    {
                        log.Fine("No Org Supervisor");
                        //	Get Parent Org Supervisor
                        if (orgInfo.GetParent_Org_ID() != 0)
                        {
                            org = MOrg.Get(GetCtx(), orgInfo.GetParent_Org_ID());
                            orgInfo = org.GetInfo();
                            if (orgInfo.GetSupervisor_ID() != 0)
                            {
                                user = MUser.Get(GetCtx(), orgInfo.GetSupervisor_ID());
                                log.Fine("Parent Org Supervisor: " + user.GetName());
                            }
                        }
                    }
                }	//	No Supervisor

            }	//	while there is a user to approve

            log.Fine("No user found");
            return -1;
        }

        /// <summary>
        /// Execute Work.
        /// Called from MWFProcess.StartNext
        /// Feedback to Process via SetWFState -> CheckActivities
        /// </summary>
        public void Run()
        {
            log.Info(ToString());
            _newValue = null;
            if (!_state.IsValidAction(StateEngine.ACTION_START))
            {
                SetTextMsg("State=" + GetWFState() + " - cannot start");
                SetWFState(StateEngine.STATE_TERMINATED);
                return;
            }
            //
            SetWFState(StateEngine.STATE_RUNNING);
            _trx = Trx.Get(Trx.CreateTrxName("WF"));

            //
            try
            {
                if (GetNode().Get_ID() == 0)
                {
                    SetTextMsg("Node not found - AD_WF_Node_ID=" + GetAD_WF_Node_ID());
                    SetWFState(StateEngine.STATE_ABORTED);
                    return;
                }
                //	Do Work
                /****	Trx Start	****/
                //	log.config("*Start " + toString() + " - " + _trx.getTrxName());
                bool done = PerformWork(_trx);
                /****	Trx End		****/
                //	log.config("*Commit " + toString() + " - " + _trx.getTrxName());
                _trx.Commit();
                _trx.Close();
                _trx = null;
                //
                //	log.config("*State " + toString());
                SetWFState(done ? StateEngine.STATE_COMPLETED : StateEngine.STATE_SUSPENDED);
                //	log.config("*Done  " + toString());
                //
                if (_postImmediate != null)
                {
                    PostImmediate();
                }
            }
            catch (Exception e)
            {
                log.Log(Level.WARNING, "" + GetNode(), e);
                /****	Trx Rollback	****/
                _trx.Rollback();
                _trx.Close();
                _trx = null;
                //
                if (e.Message != null)
                {
                    log.Log(Level.WARNING, "Cause", e.Message);
                }
                String processMsg = e.Message;
                //if (processMsg == null || processMsg.Length == 0)
                //    processMsg = e.getMessage();
                SetTextMsg(processMsg);
                AddTextMsg(e);
                SetWFState(StateEngine.STATE_TERMINATED);	//	unlocks
                //	Set Document Status 
                if (_po != null && _docStatus != null)
                {
                    _po.Load((Trx)null);
                    DocAction doc = (DocAction)_po;
                    //doc.SetDocStatus(_docStatus);
                    doc.SetDocStatus(Util.GetValueOfString(_po.Get_Value("DocStatus")));
                    _po.Save();
                }
            }
            _trx = null;
        }

        /// <summary>
        /// Perform Work. Set Text Msg.
        /// </summary>
        /// <param name="trx">transaction</param>
        /// <returns>true if completed, false otherwise and throws Exception if error</returns>
        private bool PerformWork(Trx trx)
        {
            ReportCtl.Report = null;
            AD_ReportFormat_ID = 0;

            log.Info(_node + " [" + trx + "]");
            _postImmediate = null;
            _docStatus = null;
            if (_node.GetPriority() != 0)		//	overwrite priority if defined
                SetPriority(_node.GetPriority());
            String action = _node.GetAction();

            /******	Sleep (Start/End)			******/
            if (MWFNode.ACTION_WaitSleep.Equals(action))
            {
                log.Fine("Sleep:WaitTime=" + _node.GetWaitTime());
                if (_node.GetWaitingTime() == 0)
                    return true;	//	done

                //java.util.Calendar cal = java.util.Calendar.getInstance();
                //cal.add(_node.GetDurationCalendarField(), _node.GetWaitTime());
                //SetEndWaitTime(new Timestamp(cal.getTimeInMillis()));

                DateTime dtTime = CommonFunctions.AddDate(_node.GetDurationCalendarField(), _node.GetWaitTime());
                SetEndWaitTime(dtTime);
                return false;		//	not done
            }

            /******	Document Action				******/
            else if (MWFNode.ACTION_DocumentAction.Equals(action))
            {
                log.Fine("DocumentAction=" + _node.GetDocAction());
                GetPO(trx);
                if (_po == null)
                    throw new Exception("Persistent Object not found - AD_Table_ID="
                        + GetAD_Table_ID() + ", Record_ID=" + GetRecord_ID());
                _po.Set_TrxName(trx);
                bool success = false;
                String processMsg = null;
                DocAction doc = null;
                if (_po.GetType() == typeof(DocAction) || _po.GetType().GetInterface("DocAction") == typeof(DocAction))
                {
                    doc = (DocAction)_po;
                    //
                    
                    success = doc.ProcessIt(_node.GetDocAction());	//	MOrder ** Do the work


                    #region Commented Budget Control
                    // Check Added to Enable Budget Control
                    //if (DocActionVariables.ACTION_PREPARE.Equals(_node.GetDocAction()) || (_node.GetDocAction().Equals("--")))
                    //{
                    //    try
                    //    {
                    //        if (GetAD_Table_ID().Equals(259))
                    //        {
                    //            if (!(((MOrder)(doc)).IsSOTrx()) && !(((MOrder)(doc)).IsReturnTrx()))
                    //            {
                    //                string sqlChkBud = "SELECT IsBudgetEnabled FROM AD_Org WHERE AD_Org_ID = " + GetAD_Org_ID();
                    //                string budChk = Util.GetValueOfString(DB.ExecuteScalar(sqlChkBud, null, null));
                    //                if (budChk.Equals("Y"))
                    //                {
                    //                    ModelLibrary.Classes.BudgetCheck bc = new ModelLibrary.Classes.BudgetCheck();
                    //                    bc.AD_Table_ID = GetAD_Table_ID();
                    //                    bc.Record_ID = GetRecord_ID();
                    //                    bc.TrxName = trx;
                    //                    bc.AD_Client_ID = doc.GetAD_Client_ID();
                    //                    bc.AD_Org_ID = doc.GetAD_Org_ID();
                    //                    bc.DocAction = _node.GetDocAction();
                    //                    bc.CheckBudget();
                    //                }
                    //            }
                    //        }
                    //    }
                    //    catch
                    //    {

                    //    }
                    //}
                    #endregion

                    SetTextMsg(doc.GetSummary());
                    processMsg = doc.GetProcessMsg();
                    _docStatus = doc.GetDocStatus();
                    //	Post Immediate
                    if (success && DocActionVariables.ACTION_COMPLETE.Equals(_node.GetDocAction()))
                    {
                        MClient client = MClient.Get(_po.GetCtx(), doc.GetAD_Client_ID());
                        if (client.IsPostImmediate())
                            _postImmediate = doc;
                    }
                    //
                    if (_process != null)
                        _process.SetProcessMsg(processMsg);
                }
                else
                {
                    //throw new IllegalStateException("Persistent Object not DocAction - "
                    //    + _po.getClass().getName()
                    //    + " - AD_Table_ID=" + GetAD_Table_ID() + ", Record_ID=" + GetRecord_ID());
                    throw new Exception("Persistent Object not DocAction"
                        + " - AD_Table_ID=" + GetAD_Table_ID() + ", Record_ID=" + GetRecord_ID());
                }
                //
                if (!_po.Save())
                {
                    success = false;
                    processMsg = "SaveError";
                }
                if (!success)
                {
                    if (processMsg == null || processMsg.Length == 0)
                    {
                        processMsg = "PerformWork Error - " + _node.ToStringX();
                        if (doc != null)	//	problem: status will be rolled back
                            processMsg += " - DocStatus=" + doc.GetDocStatus();
                    }
                    throw new Exception(processMsg);
                }
                return success;
            }	//	DocumentAction

            /******	Report						******/
            else if (MWFNode.ACTION_AppsReport.Equals(action))
            {
                log.Fine("Report:AD_Process_ID=" + _node.GetAD_Process_ID());
                //	Process
                MProcess process = MProcess.Get(GetCtx(), _node.GetAD_Process_ID());

                if (!process.IsReport() || (process.GetAD_PrintFormat_ID() == 0
                                             && process.GetAD_ReportView_ID() == 0
                                             && !process.IsCrystalReport()
                                              && process.GetAD_ReportFormat_ID() == 0))

                // if (!process.IsReport() || process.GetAD_ReportView_ID() == 0)
                {
                    //throw new IllegalStateException("Not a Report AD_Process_ID=" + _node.getAD_Process_ID());
                    throw new Exception("Not a Report AD_Process_ID=" + _node.GetAD_Process_ID());
                }
                //
                ProcessInfo pi = new ProcessInfo(_node.GetName(true), _node.GetAD_Process_ID(),
                    GetAD_Table_ID(), GetRecord_ID());
                pi.SetAD_User_ID(GetAD_User_ID());
                pi.SetAD_Client_ID(GetAD_Client_ID());
                MPInstance pInstance = new MPInstance(process, GetRecord_ID());
                FillParameter(pInstance, trx);
                pi.SetAD_PInstance_ID(pInstance.GetAD_PInstance_ID());
                //	Report
                //ReportEngine_N re = ReportEngine_N.Get(GetCtx(), pi);
                //if (re == null)
                //{
                //    //throw new IllegalStateException("Cannot create Report AD_Process_ID=" + _node.getAD_Process_ID());
                //    throw new Exception("Cannot create Report AD_Process_ID=" + _node.GetAD_Process_ID());
                //}
                pi.SetIsCrystal(process.IsCrystalReport());
                AD_ReportFormat_ID = process.GetAD_ReportFormat_ID();
                ReportRun(pi);
                SendNoticeEMail(trx);



                return true;
            }

            /******	Process						******/
            else if (MWFNode.ACTION_AppsProcess.Equals(action))
            {
                log.Fine("Process:AD_Process_ID=" + _node.GetAD_Process_ID());
                //	Process
                MProcess process = MProcess.Get(GetCtx(), _node.GetAD_Process_ID());
                MPInstance pInstance = new MPInstance(process, GetRecord_ID());
                FillParameter(pInstance, trx);
                //
                ProcessInfo pi = new ProcessInfo(_node.GetName(true), _node.GetAD_Process_ID(),
                    GetAD_Table_ID(), GetRecord_ID());
                pi.SetAD_User_ID(GetAD_User_ID());
                pi.SetAD_Client_ID(GetAD_Client_ID());
                pi.SetAD_PInstance_ID(pInstance.GetAD_PInstance_ID());
                return process.ProcessIt(pi, trx);
            }

            /******	TODO Start Task				******/
            else if (MWFNode.ACTION_AppsTask.Equals(action))
            {
                log.Warning("Task:AD_Task_ID=" + _node.GetAD_Task_ID());
            }

            /******	EMail						******/
            else if (MWFNode.ACTION_EMail.Equals(action))
            {
                log.Fine("EMail:EMailRecipient=" + _node.GetEMailRecipient());
                GetPO(trx);
                if (_po == null)
                    throw new Exception("Persistent Object not found - AD_Table_ID="
                        + GetAD_Table_ID() + ", Record_ID=" + GetRecord_ID());
                // if (_po.GetType() == typeof(DocAction) || _po.GetType().GetInterface("DocAction", true) == typeof(DocAction))
                // {
                _emails = new List<String>();
                SendEMail(action);
                StringBuilder sbEmail = new StringBuilder();
                for (int i = 0; i < _emails.Count; i++)
                {
                    if (i == 0)
                        sbEmail.Append(_emails[i]);
                    else
                        sbEmail.Append(", " + _emails[i]);
                }

                SetTextMsg(sbEmail.ToString() +"  "+SaveActionLog(sbEmail.ToString(),Get_Trx()));
                //  }
                //  else
                //  {
                //   MClient client = MClient.Get(GetCtx(), GetAD_Client_ID());
                //   MMailText mailtext = new MMailText(GetCtx(), GetNode().GetR_MailText_ID(), null);

                //   String subject = GetNode().GetDescription()
                //   + ": " + mailtext.GetMailHeader();

                //    String message = mailtext.GetMailText(true)
                //     + "\n-----\n" + GetNodeHelp();
                //    String to = GetNode().GetEMail();

                //   //client.SendEMail(to,GetNode().GetName(), subject, message, null);
                //   // Commented By Lokesh Chauhan bcoz not picking the from Email (client.GetEMailTest())
                //   //client.SendEMail(client, client.GetEMailTest(), client.GetName(), to, GetNode().GetName(), subject, message, Envs.GetCtx());
                //  client.SendEMail(client, client.GetRequestEMail(), client.GetName(), to, GetNode().GetName(), subject, message, Envs.GetCtx());

                // }

                return true;	//	done
            }	//	EMail

            /******	Set Variable				******/
            else if (MWFNode.ACTION_SetVariable.Equals(action))
            {
                String value = _node.GetAttributeValue();
                log.Fine("SetVariable:AD_Column_ID=" + _node.GetAD_Column_ID() + " to " + value);
                MColumn column = _node.GetColumn();
                int dt = column.GetAD_Reference_ID();
                return SetVariable(value, dt, null);
            }

            /******	TODO Start WF Instance		******/
            else if (MWFNode.ACTION_SubWorkflow.Equals(action))
            {
                log.Warning("Workflow:AD_Workflow_ID=" + _node.GetAD_Workflow_ID());
            }

            /******	User Choice					******/
            else if (MWFNode.ACTION_UserChoice.Equals(action))
            {
                log.Fine("UserChoice:AD_Column_ID=" + _node.GetAD_Column_ID());
                //	Approval
                if (_node.IsUserApproval() && (GetPO().GetType() == typeof(DocAction) || GetPO().GetType().GetInterface("DocAction") == typeof(DocAction)))
                {
                    DocAction doc = (DocAction)_po;
                    bool autoApproval = false;
                    //	Approval Hierarchy
                    if (IsInvoker())
                    {
                        //	Set Approver
                        int startAD_User_ID = GetAD_User_ID();
                        if (startAD_User_ID == 0)
                            startAD_User_ID = doc.GetDoc_User_ID();
                        int nextAD_User_ID = GetApprovalUser(startAD_User_ID,
                            doc.GetC_Currency_ID(), doc.GetApprovalAmt(),
                            doc.GetAD_Org_ID(),
                            startAD_User_ID == doc.GetDoc_User_ID());	//	own doc
                        //	same user = approved
                        autoApproval = startAD_User_ID == nextAD_User_ID;
                        if (!autoApproval)
                            SetAD_User_ID(nextAD_User_ID);
                        //Lakhwinder
                        if (GetAD_User_ID() == 0)
                        {
                            nextAD_User_ID = Util.GetValueOfInt(DB.ExecuteScalar("SELECT Supervisor_ID FROM AD_User WHERE IsActive='Y' AND AD_User_ID=" + p_ctx.GetAD_User_ID()));
                            SetAD_User_ID(nextAD_User_ID);

                        }
                    }
                    else	//	fixed Approver
                    {
                        MWFResponsible resp = GetResponsible();
                        autoApproval = resp.GetAD_User_ID() == GetAD_User_ID();
                        if (!autoApproval && resp.GetAD_User_ID() != 0)
                            SetAD_User_ID(resp.GetAD_User_ID());
                    }

                    if (autoApproval
                        && doc.ProcessIt(DocActionVariables.ACTION_APPROVE)
                        && doc.Save())
                        return true;	//	done
                }	//	approval

                else if (_node.IsMultiApproval() && _node.GetApprovalLeval() > -1) //For Leave Request Approval
                {
                    DocAction doc = null;
                    if ((GetPO().GetType() == typeof(DocAction)))
                    {
                        doc = (DocAction)_po;
                    }
                    //bool autoApproval = false;
                    int startAD_User_ID = 0;
                    int evendID = Util.GetValueOfInt(DB.ExecuteScalar(@"SELECT WFE.AD_WF_EventAudit_ID
                                                                    FROM AD_WF_EventAudit WFE
                                                                    INNER JOIN AD_WF_Process WFP
                                                                    ON (WFP.AD_WF_Process_ID=WFE.AD_WF_Process_ID)
                                                                    INNER JOIN AD_WF_Activity WFA
                                                                    ON (WFA.AD_WF_Process_ID =WFP.AD_WF_Process_ID)
                                                                    WHERE WFE.AD_WF_Node_ID  =" + GetAD_WF_Node_ID() + @"
                                                                    AND WFA.AD_WF_Activity_ID=" + GetAD_WF_Activity_ID()));
                    MWFEventAudit eve = new MWFEventAudit(GetCtx(), evendID, null);
                    if (_node.GetAD_Column_ID_3() > 0)
                    {
                        startAD_User_ID = Util.GetValueOfInt(_po.Get_Value((new MColumn(GetCtx(), _node.GetAD_Column_ID_3(), null).GetColumnName())));

                    }
                    else if (startAD_User_ID == 0)
                    {
                        startAD_User_ID = GetAD_User_ID();
                    }
                    if (doc != null && startAD_User_ID == 0)
                        startAD_User_ID = doc.GetDoc_User_ID();
                    if (_node.GetApprovalLeval() == 0)
                    {
                        SetAD_User_ID(startAD_User_ID);
                        eve.SetAD_User_ID(startAD_User_ID);
                        eve.Save();
                        return true;
                    }

                    SetAD_User_ID(startAD_User_ID);
                    int nextAD_User_ID = Util.GetValueOfInt(DB.ExecuteScalar("SELECT Supervisor_ID FROM AD_User WHERE IsActive='Y' AND AD_User_ID=" + startAD_User_ID));
                    //	same user = approved
                    //autoApproval = startAD_User_ID == nextAD_User_ID;
                    if (nextAD_User_ID == 0)
                    {
                        SetAD_User_ID(startAD_User_ID);
                        eve.SetAD_User_ID(startAD_User_ID);
                        eve.Save();
                        return true;
                    }
                    SetAD_User_ID(nextAD_User_ID);
                    eve.SetAD_User_ID(nextAD_User_ID);
                    eve.Save();
                    //if (!autoApproval)
                    //    SetAD_User_ID(nextAD_User_ID);
                    //if (autoApproval)
                    //{
                    //    if (doc != null)
                    //    {
                    //        doc.ProcessIt(DocActionVariables.ACTION_APPROVE);
                    //        doc.Save();
                    //    }
                    //    MColumn column = _node.GetColumn();
                    //    int dt = column.GetAD_Reference_ID();
                    //    SetVariable("Y", dt, null);
                    //    return true;
                    //}
                }

                else
                {
                    int evendID = Util.GetValueOfInt(DB.ExecuteScalar(@"SELECT WFE.AD_WF_EventAudit_ID
                                                                    FROM AD_WF_EventAudit WFE
                                                                    INNER JOIN AD_WF_Process WFP
                                                                    ON (WFP.AD_WF_Process_ID=WFE.AD_WF_Process_ID)
                                                                    INNER JOIN AD_WF_Activity WFA
                                                                    ON (WFA.AD_WF_Process_ID =WFP.AD_WF_Process_ID)
                                                                    WHERE WFE.AD_WF_Node_ID  =" + GetAD_WF_Node_ID() + @"
                                                                    AND WFA.AD_WF_Activity_ID=" + GetAD_WF_Activity_ID()));
                    MWFEventAudit eve = new MWFEventAudit(GetCtx(), evendID, null);
                    int userID = 0;

                    if (_node.GetAD_WF_Responsible_ID() > 0)
                    {
                        userID = (GetUserFromWFResponsible(_node.GetAD_WF_Responsible_ID(), GetPO()));
                    }
                    else if (_node.GetWorkflow().GetAD_WF_Responsible_ID() > 0)
                    {
                        userID = (GetUserFromWFResponsible(_node.GetWorkflow().GetAD_WF_Responsible_ID(), GetPO()));
                    }
                    else
                    {
                        userID = (GetCtx().GetAD_User_ID());
                    }
                    SetAD_User_ID(userID);
                    eve.SetAD_User_ID(userID);
                    eve.Save();
                }
                //For Genral Attribute
                //else if (new MColumn(GetCtx(),_node.GetAD_Column_ID(),Get_TrxName()).GetColumnName().ToUpper().Equals("C_GENATTRIBUTESETINSTANCE_ID"))
                //{

                //}
                return false;	//	wait for user
            }
            /******	User Workbench				******/
            else if (MWFNode.ACTION_UserWorkbench.Equals(action))
            {
                log.Fine("Workbench:?");
                return false;
            }
            /******	User Form					******/
            else if (MWFNode.ACTION_UserForm.Equals(action))
            {
                log.Fine("Form:AD_For_ID=" + _node.GetAD_Form_ID());
                return false;
            }
            /******	User Window					******/
            else if (MWFNode.ACTION_UserWindow.Equals(action))
            {
                log.Fine("Window:AD_Window_ID=" + _node.GetAD_Window_ID());
                return false;
            }


              /********** SendSms ***********/
            else if (MWFNode.ACTION_SMS.Equals(action))
            {
                GetPO(trx);
                if (_po == null)
                    throw new Exception("Persistent Object not found - AD_Table_ID="
                        + GetAD_Table_ID() + ", Record_ID=" + GetRecord_ID());
                SendSms();
                return true;
            }

            /************** FAX **********/
            else if (MWFNode.ACTION_FaxEMail.Equals(action))
            {
                //log.Fine("EMail:EMailRecipient=" + _node.GetEMailRecipient());
                GetPO(trx);
                if (_po == null)
                    throw new Exception("Persistent Object not found - AD_Table_ID="
                        + GetAD_Table_ID() + ", Record_ID=" + GetRecord_ID());
                if (_po.GetType() == typeof(DocAction) || _po.GetType().GetInterface("DocAction", true) == typeof(DocAction))
                {
                    _emails = new List<String>();
                    SendFaxEMail();
                    StringBuilder sbEmail = new StringBuilder();
                    for (int i = 0; i < _emails.Count; i++)
                    {
                        if (i == 0)
                            sbEmail.Append(_emails[i]);
                        else
                            sbEmail.Append(", " + _emails[i]);
                    }
                    SetTextMsg(sbEmail.ToString());
                }
                else
                {
                    MClient client = MClient.Get(GetCtx(), GetAD_Client_ID());
                    MMailText mailtext = new MMailText(GetCtx(), GetNode().GetR_MailText_ID(), null);

                    String subject = GetNode().GetDescription()
                    + ": " + mailtext.GetMailHeader();

                    String message = mailtext.GetMailText(true)
                    + "\n-----\n" + GetNodeHelp();
                    String to = GetNode().GetEMail();

                    //client.SendEMail(to,GetNode().GetName(), subject, message, null);
                    client.SendEMail(to, GetNode().GetName(), subject, message, null);

                }
                return true;
            }
            /************** EMail&FAX *********/
            else if (MWFNode.ACTION_EMailPlusFaxEMail.Equals(action))
            {
                ///////////Email//////////
                log.Fine("EMail:EMailRecipient=" + _node.GetEMailRecipient());
                GetPO(trx);
                if (_po == null)
                    throw new Exception("Persistent Object not found - AD_Table_ID="
                        + GetAD_Table_ID() + ", Record_ID=" + GetRecord_ID());
                //if (_po.GetType() == typeof(DocAction) || _po.GetType().GetInterface("DocAction", true) == typeof(DocAction))
                // {
                _emails = new List<String>();
                SendEMail(action);
                StringBuilder sbEmail = new StringBuilder();
                for (int i = 0; i < _emails.Count; i++)
                {
                    if (i == 0)
                        sbEmail.Append(_emails[i]);
                    else
                        sbEmail.Append(", " + _emails[i]);
                }
                SetTextMsg(sbEmail.ToString() +"  "+ SaveActionLog(sbEmail.ToString(),Get_TrxName()));
                // }
                // else
                // {
                //    MClient client = MClient.Get(GetCtx(), GetAD_Client_ID());
                //    MMailText mailtext = new MMailText(GetCtx(), GetNode().GetR_MailText_ID(), null);

                //    String subject = GetNode().GetDescription()
                //    + ": " + mailtext.GetMailHeader();

                //     String message = mailtext.GetMailText(true)
                //    + "\n-----\n" + GetNodeHelp();
                //    String to = GetNode().GetEMail();

                //    //client.SendEMail(to,GetNode().GetName(), subject, message, null);
                //    client.SendEMail(to, GetNode().GetName(), subject, message, null);

                // }
                ///////////Email//////////


                ///////////FaxEmail//////////
                GetPO(trx);
                if (_po == null)
                    throw new Exception("Persistent Object not found - AD_Table_ID="
                        + GetAD_Table_ID() + ", Record_ID=" + GetRecord_ID());
                if (_po.GetType() == typeof(DocAction) || _po.GetType().GetInterface("DocAction", true) == typeof(DocAction))
                {
                    _emails = new List<String>();
                    SendFaxEMail();
                    StringBuilder sbEmail1 = new StringBuilder();
                    for (int i = 0; i < _emails.Count; i++)
                    {
                        if (i == 0)
                            sbEmail1.Append(_emails[i]);
                        else
                            sbEmail1.Append(", " + _emails[i]);
                    }
                    SetTextMsg(sbEmail1.ToString());
                }
                else
                {
                    MClient client = MClient.Get(GetCtx(), GetAD_Client_ID());
                    MMailText mailtext = new MMailText(GetCtx(), GetNode().GetR_MailText_ID(), null);

                    String subject = GetNode().GetDescription()
                    + ": " + mailtext.GetMailHeader();

                    String message = mailtext.GetMailText(true)
                    + "\n-----\n" + GetNodeHelp();
                    String to = GetNode().GetEMail();

                    //client.SendEMail(to,GetNode().GetName(), subject, message, null);
                    client.SendEMail(to, GetNode().GetName(), subject, message, null);

                }
                ///////////FaxEmail//////////


                return true;
            }

             //Forward Document
            else if (MWFNode.ACTION_ForwardDocument.Equals(action))
            {
                log.Fine("ForwardDocument:?");
                string res = string.Empty;
                GetPO(Get_TrxName());
                DocumentAction docAction = new DocumentAction();
                bool ok = docAction.ForwardDocument(GetRecipientUser(), (int)_po.Get_Value("VADMS_Document_ID"), GetCtx(), out res);
                SetTextMsg(res);
                return ok;
            }
            //Move documnet
            else if (MWFNode.ACTION_MoveDocument.Equals(action))
            {
                log.Fine("MoveDocument:");
                string res = string.Empty;
                GetPO(Get_TrxName());
                DocumentAction docAction = new DocumentAction();
                bool ok = docAction.MoveDocument((int)_po.Get_Value("VADMS_Document_ID"), _node.GetVADMS_Folder_ID(), _node.GetVADMS_Folder_ID_1(), GetCtx(), out res);
                SetTextMsg(res);
                return ok;
            }

            //Access Document
            else if (MWFNode.ACTION_AccessDocument.Equals(action))
            {
                string res = string.Empty;
                GetPO(Get_TrxName());
                DocumentAction docAction = new DocumentAction();
                bool ok = docAction.AllocateAccess(GetRecipientUserOnly(),GetRecipientRoles(), (int)_po.Get_Value("VADMS_Document_ID"), _node.GetVADMS_Access(), GetCtx(), out res);
                SetTextMsg(res);
                return ok;
            }
            //
            //
            throw new ArgumentException("Invalid Action (Not Implemented) =" + action);
        }

        volatile IReportEngine re = null;
        int AD_ReportFormat_ID = 0;
        /// <summary>
        /// Report through work order
        /// </summary>
        /// <param name="_pi"></param>
        public void ReportRun(ProcessInfo _pi)
        {
            //  start report code
            ///	Start Report	-----------------------------------------------
            ///	

            //if("Y".Equals(DB.ExecuteScalar("SELECT IsCrystalReport FROM AD_Process WHERE AD_Process_ID = "+_pi.GetAD_Process_ID())))

            if (AD_ReportFormat_ID > 0)
            {
                try
                {
                    string lang = p_ctx.GetAD_Language().Replace("_", "-");

                    if ((AD_ReportFormat_ID > 0) && (lang == "ar-IQ"))
                    {
                        isDocxFile = true;
                        re = VAdvantage.ReportFormat.ReportFormatEngine.Get(p_ctx, _pi, true);

                    }
                    else
                    {
                        re = VAdvantage.ReportFormat.ReportFormatEngine.Get(p_ctx, _pi, false);
                    }
                }
                catch
                {
                    re = null;
                }

            }

            else if (_pi.GetIsCrystal())
            {
                //_pi.SetIsCrystal(true);
                try
                {
                    re = new VAdvantage.CrystalReport.CrystalReportEngine(p_ctx, _pi);
                }
                catch
                {
                    re = null;
                }
            }
            else
            {
                re = ReportCtl.Start(p_ctx, _pi, false);
            }

            _pi.SetSummary("Report", re != null);

            ReportCtl.Report = re;
        }

        bool isDocxFile = false;

        private void SendNoticeEMail(Trx trx)
        {
            //	Notice
            int AD_Message_ID = 753;		//	HARDCODED WorkflowResul
            List<int> userIds = new List<int>();
            StringBuilder sbLog = new StringBuilder("");
            DocAction doc = null;
            bool isPOAsDocAction = false;
            bool hasMailTemplate = false;
            MMailText text = null;

            _emails = new List<String>();

            StringBuilder sbEmail = new StringBuilder();
            byte[] report = null;

            if (re != null) //create report
            {
                if (re is IReportView)
                {
                    ((IReportView)re).GetView();
                }

                report = re.GetReportBytes();
            }




            MWFResponsible resp = GetResponsible(); //Get WF responsible
            if (resp.IsInvoker())
            {
                userIds.Add(GetAD_User_ID());
            }
            else if (resp.IsHuman())
            {
                //   SendEMail(client, resp.GetAD_User_ID(), null, subject, message, data, isHTML, tableID, RecID);
                userIds.Add(resp.GetAD_User_ID());
            }
            else if (resp.IsRole())
            {
                MRole role = resp.GetRole();
                if (role != null)
                {
                    MUser[] users = MUser.GetWithRole(role);
                    for (int i = 0; i < users.Length; i++)
                    {
                        userIds.Add(users[i].GetAD_User_ID());
                        //SendEMail(client, users[i].GetAD_User_ID(), null, subject, message, data, isHTML, tableID, RecID);
                    }
                }
            }
            else if (resp.IsOrganization())
            {
                MOrgInfo org = MOrgInfo.Get(GetCtx(), _po.GetAD_Org_ID(), null);
                if (org.GetSupervisor_ID() == 0)
                {
                    sbLog.Append("No Supervisor for AD_Org_ID=" + _po.GetAD_Org_ID());
                }
                else
                {
                    userIds.Add(org.GetSupervisor_ID());
                    //  SendEMail(client, org.GetSupervisor_ID(), null, subject, message, data, isHTML, tableID, RecID);
                }
            }


            /******************************************************/

            if (_po is DocAction) //MClass Implement DocAction
            {
                doc = (DocAction)_po;
                isPOAsDocAction = true;
            }

            if (_node.GetR_MailText_ID() > 0)
            {
                hasMailTemplate = true;
                text = new MMailText(GetCtx(), _node.GetR_MailText_ID(), null);
                text.SetPO(_po, true); //Set _Po Current value
            }

            bool isHTML = hasMailTemplate ? text.IsHtml() : true;

            String subject = "";
            if (isPOAsDocAction)
            {
                subject += doc.GetDocumentInfo();
            }
            else
            {
                subject += GetNode().GetDescription();
            }
            subject += hasMailTemplate ? ": " + text.GetMailHeader() : "";

            String message = hasMailTemplate ? text.GetMailText(true) : "";

            if (isPOAsDocAction)
            {
                message += "\n-----\n" + doc.GetDocumentInfo()
                 + "\n" + doc.GetSummary();
            }
            else
            {
                message += "\n-----\n" + GetNodeHelp();
            }

            FileInfo pdf = null;
            if (isPOAsDocAction)
            {
                pdf = doc.CreatePDF();
            }

            //byte[] data = null;
            //if (pdf != null)
            //{
            //    Stream stream = pdf.OpenRead();
            //    data = new byte[stream.Length];
            //    stream.Read(data, 0, data.Length);
            //}
            //
            MClient client = MClient.Get(_po.GetCtx(), isPOAsDocAction ? doc.GetAD_Client_ID() : _po.GetAD_Client_ID());

            //	Explicit EMail

            string nodeEmails = _node.GetEMail() ?? "";
            if (nodeEmails.IndexOf("@EMail@") != -1) //Get from PO
            {
                nodeEmails = nodeEmails.Replace("@EMail@", ParseVariable("EMail", _po));
            }

            //SendEMail(client, 0, nodeEmails, subject, message, , isHTML, 0, 0);
            SendEMail(client, 0, nodeEmails, subject, message, pdf, isHTML, 0, 0,MWFNode.ACTION_AppsReport, report);

            /******************************************************/



            for (int i = 0; i < userIds.Count; i++)
            {
                // Notice 
                MUser user = MUser.Get(p_ctx, userIds[i]);
                if (user.IsActive())
                {
                    if (user.IsNotificationNote() || user.GetNotificationType() == MUser.NOTIFICATIONTYPE_EMailPlusNotice)
                    {
                        MNote note = new MNote(GetCtx(), AD_Message_ID, userIds[i], trx);
                        note.SetTextMsg(_node.GetName(true));
                        note.SetDescription(_node.GetDescription(true));
                        note.SetRecord(GetAD_Table_ID(), GetRecord_ID());
                        note.Save();
                        ////	Attachment
                        if (report != null)
                        {
                            MAttachment attachment = new MAttachment(GetCtx(), MNote.Table_ID, note.GetAD_Note_ID(), trx);
                            if (isDocxFile)
                            {
                                attachment.AddEntry("Report_" + DateTime.Now.Ticks + ".Docx", report);
                            }
                            else
                            {
                                attachment.AddEntry("Report_" + DateTime.Now.Ticks + ".pdf", report);
                            }
                            attachment.SetTextMsg(_node.GetName(true));
                            attachment.Save();
                        }
                        sbLog.Append(",").Append(user.GetName()).Append("-OK");
                    }
                    else
                    {
                        sbLog.Append(",NoteNotSet=>" + user.GetName() + "]");
                    }

                    SendEMail(client, userIds[i], null, subject, message, pdf, isHTML, 0, 0, MWFNode.ACTION_AppsReport, report);
                }
                else
                {
                    sbLog.Append(",NotSendNoticeEmail[InActiveUser=>" + user.GetName() + "]");
                }
            }

            for (int ii = 0; ii < _emails.Count; ii++)
            {
                if (ii == 0)
                    sbEmail.Append(_emails[ii]);
                else
                    sbEmail.Append(", " + _emails[ii]);
            }

            SetTextMsg(sbLog + " || Email -" + sbEmail.ToString());
        }

        /// <summary>
        /// Set Variable
        /// </summary>
        /// <param name="value">new Value</param>
        /// <param name="displayType">display type</param>
        /// <param name="textMsg">optional Message</param>
        /// <returns>true if set and throws Exception if error</returns>
        private bool SetVariable(String value, int displayType, String textMsg)
        {
            _newValue = null;
            GetPO();
            if (_po == null)
                throw new Exception("Persistent Object not found - AD_Table_ID="
                    + GetAD_Table_ID() + ", Record_ID=" + GetRecord_ID());

            //Check For Genral Attribute 
            MColumn column = new MColumn(GetCtx(), GetNode().GetAD_Column_ID(), Get_TrxName());
            if (column.GetColumnName().ToString().ToLower().Equals("c_genattributesetinstance_id"))
            {

                int attributeSetID = GetNode().GetC_GenAttributeSet_ID();
                int attributeSetInstanceID = Util.GetValueOfInt(GetNode().GetC_GenAttributeSetInstance_ID());
                if (attributeSetID == 0 && attributeSetInstanceID == 0)//user Choice
                {
                    _po.Set_Value("C_GenAttributeSetInstance_ID", value);
                    _newValue = Util.GetValueOfString(DB.ExecuteScalar(@"SELECT GS.Name || '->' || GSI.Description
                                                FROM C_GenAttributeSetInstance GSI
                                                INNER JOIN C_GenAttributeSet GS
                                                ON (GS.C_GenAttributeSet_ID        =GSI.C_GenAttributeSet_ID)
                                                WHERE GSI.IsActive                 ='Y'                                              
                                                AND GSI.C_GenAttributeSetInstance_ID=" + value));

                }
                else  //SetVariable
                {
                    _po.Set_Value("C_GenAttributeSet_ID", GetNode().GetC_GenAttributeSet_ID());
                    _po.Set_Value("C_GenAttributeSetInstance_ID", GetNode().GetC_GenAttributeSetInstance_ID());
                    _newValue = Util.GetValueOfString(DB.ExecuteScalar(@"SELECT GS.Name || '->' || GSI.Description
                                                FROM C_GenAttributeSetInstance GSI
                                                INNER JOIN C_GenAttributeSet GS
                                                ON (GS.C_GenAttributeSet_ID        =GSI.C_GenAttributeSet_ID)
                                                WHERE GSI.IsActive                 ='Y'
                                                AND GS.C_GenAttributeSet_ID       =" + _po.Get_Value("C_GenAttributeSet_ID") + @"
                                                AND GSI.C_GenAttributeSetInstance_ID=" + _po.Get_Value("C_GenAttributeSetInstance_ID")));
                }
                SetTextMsg("New value assigned to Attribute " + _newValue);

                return _po.Save();
            }

            //	Set Value
            Object dbValue = null;
            if (value == null)
            {
            }
            else if (displayType == DisplayType.YesNo)
                dbValue = Convert.ToBoolean("Y".Equals(value));
            else if (DisplayType.IsNumeric(displayType))
                dbValue = Convert.ToDecimal(value);
            else
                dbValue = value;
            _po.Set_ValueOfColumn(GetNode().GetAD_Column_ID(), dbValue);
            _po.Save();
            Object dbValueNew = _po.Get_ValueOfColumn(GetNode().GetAD_Column_ID());
            if (!dbValue.Equals(dbValueNew))
            {
                if (!value.Equals(dbValueNew))
                    throw new Exception("Persistent Object not updated - AD_Table_ID="
                            + GetAD_Table_ID() + ", Record_ID=" + GetRecord_ID()
                            + " - Should=" + value + ", Is=" + dbValueNew);
            }
            //	Info
            String msg = GetNode().GetAttributeName() + "=" + value;
            if (textMsg != null && textMsg.Length > 0)
                msg += " - " + textMsg;
            SetTextMsg(msg);
            _newValue = value;
            return true;
        }

        /// <summary>
        /// Set User Choice
        /// </summary>
        /// <param name="AD_User_ID">user id</param>
        /// <param name="value">new Value</param>
        /// <param name="displayType">display type</param>
        /// <param name="textMsg">optional Message</param>
        /// <returns>true if set and throws Exception if error</returns>
        public bool SetUserChoice(int AD_User_ID, String value, int displayType,
            String textMsg) //throws Exception
        {
            //	Check if user approves own document when a role is responsible
            if (GetNode().IsUserApproval() && (GetPO().GetType() == typeof(DocAction) || GetPO().GetType().GetInterface("DocAction") == typeof(DocAction)))
            {
                DocAction doc = (DocAction)_po;
                MUser user = new MUser(GetCtx(), AD_User_ID, null);
                MRole[] roles = user.GetRoles(_po.GetAD_Org_ID());
                bool canApproveOwnDoc = false;
                for (int r = 0; r < roles.Length; r++)
                {
                    if (roles[r].IsCanApproveOwnDoc())
                    {
                        canApproveOwnDoc = true;
                        break;
                    }	//	found a role which allows to approve own document
                }
                if (!canApproveOwnDoc)
                {
                    String info = user.GetName() + " cannot approve own document " + doc;
                    AddTextMsg(info);
                    log.Fine(info);
                    return false;		//	ignore
                }
            }

            SetWFState(StateEngine.STATE_RUNNING);
            SetAD_User_ID(AD_User_ID);
            bool ok = SetVariable(value, displayType, textMsg);
            if (!ok)
                return false;

            String newState = StateEngine.STATE_COMPLETED;
            //	Approval
            if (GetNode().IsUserApproval() && (GetPO().GetType() == typeof(DocAction) || GetPO().GetType().GetInterface("DocAction") == typeof(DocAction)))
            {
                DocAction doc = (DocAction)_po;
                try
                {
                    //	Not pproved
                    if (!"Y".Equals(value))
                    {
                        newState = StateEngine.STATE_ABORTED;
                        if (!(doc.ProcessIt(DocActionVariables.ACTION_REJECT)))
                            SetTextMsg("Cannot Reject - Document Status: " + doc.GetDocStatus());
                    }
                    else
                    {
                        if (IsInvoker())
                        {
                            int startAD_User_ID = GetAD_User_ID();
                            if (startAD_User_ID == 0)
                                startAD_User_ID = doc.GetDoc_User_ID();
                            int nextAD_User_ID = GetApprovalUser(startAD_User_ID,
                                doc.GetC_Currency_ID(), doc.GetApprovalAmt(),
                                doc.GetAD_Org_ID(),
                                startAD_User_ID == doc.GetDoc_User_ID());	//	own doc
                            //	No Approver
                            if (nextAD_User_ID <= 0)
                            {
                                newState = StateEngine.STATE_ABORTED;
                                SetTextMsg("Cannot Approve - No Approver");
                                doc.ProcessIt(DocActionVariables.ACTION_REJECT);
                            }
                            else if (startAD_User_ID != nextAD_User_ID)
                            {
                                ForwardTo(nextAD_User_ID, "Next Approver");
                                newState = StateEngine.STATE_SUSPENDED;
                            }
                            else	//	Approve
                            {
                                if (!(doc.ProcessIt(DocActionVariables.ACTION_APPROVE)))
                                {
                                    newState = StateEngine.STATE_ABORTED;
                                    SetTextMsg("Cannot Approve - Document Status: " + doc.GetDocStatus());
                                }
                            }
                        }
                        //	No Invoker - Approve
                        else if (!(doc.ProcessIt(DocActionVariables.ACTION_APPROVE)))
                        {
                            newState = StateEngine.STATE_ABORTED;
                            SetTextMsg("Cannot Approve - Document Status: " + doc.GetDocStatus());
                        }
                    }
                    doc.Save();
                }
                catch (Exception e)
                {
                    newState = StateEngine.STATE_TERMINATED;
                    SetTextMsg("User Choice: " + e.ToString());
                    log.Log(Level.WARNING, "", e);
                }
                //	Send Approval Notification
                if (newState.Equals(StateEngine.STATE_ABORTED))
                {
                    MClient client = MClient.Get(GetCtx(), doc.GetAD_Client_ID());
                    client.SendEMail(doc.GetDoc_User_ID(),
                        doc.GetDocumentInfo() + ": " + Utility.Msg.GetMsg(GetCtx(), "NotApproved", true),
                        doc.GetSummary()
                        + "\n" + doc.GetProcessMsg()
                        + "\n" + GetTextMsg(),
                        doc.CreatePDF());
                }
            }
            SetWFState(newState);
            return ok;
        }

        /// <summary>
        /// Forward To
        /// </summary>
        /// <param name="AD_User_ID">user</param>
        /// <param name="textMsg">message</param>
        /// <returns>true if forwarded</returns>
        public bool ForwardTo(int AD_User_ID, String textMsg, bool isFromActivityForm = false)
        {
            if (AD_User_ID == GetAD_User_ID())
            {
                log.Log(Level.WARNING, "Same User - AD_User_ID=" + AD_User_ID);
                return false;
            }
            //
            MUser oldUser = MUser.Get(GetCtx(), GetAD_User_ID());
            MUser user = MUser.Get(GetCtx(), AD_User_ID);
            if (user == null || user.Get_ID() == 0)
            {
                log.Log(Level.WARNING, "Does not exist - AD_User_ID=" + AD_User_ID);
                return false;
            }
            //	Update 
            SetAD_User_ID(user.GetAD_User_ID());


            if (textMsg != null)
                textMsg += " - " + GetNode().GetAttributeName() + " Froward To:" + AD_User_ID;

            SetTextMsg(textMsg);
            Save();
            //	Close up Old Event
            GetEventAudit();
            _audit.SetAD_User_ID(oldUser.GetAD_User_ID());
            _audit.SetTextMsg(GetTextMsg());
            _audit.SetAttributeName("AD_User_ID");
            _audit.SetOldValue(oldUser.GetName() + "(" + oldUser.GetAD_User_ID() + ")");
            _audit.SetNewValue(user.GetName() + "(" + user.GetAD_User_ID() + ")");
            //
            if (isFromActivityForm)
            {
                _audit.SetWFState(WFSTATE_Completed);
            }
            else
            {
                _audit.SetWFState(GetWFState());
            }
            _audit.SetEventType(MWFEventAudit.EVENTTYPE_StateChanged);
            long ms = CommonFunctions.CurrentTimeMillis();// -_audit.GetCreated().Ticks;
            _audit.SetElapsedTimeMS(Convert.ToDecimal(ms));
            _audit.Save();
            //	Create new one
            _audit = new MWFEventAudit(this);
            _audit.Save();
            return true;
        }

        /// <summary>
        /// Set User Confirmation
        /// </summary>
        /// <param name="AD_User_ID">user</param>
        /// <param name="textMsg">optional message</param>
        public void SetUserConfirmation(int AD_User_ID, String textMsg)
        {
            log.Fine(textMsg);
            SetWFState(StateEngine.STATE_RUNNING);
            SetAD_User_ID(AD_User_ID);
            if (textMsg != null)
                SetTextMsg(textMsg);
            SetWFState(StateEngine.STATE_COMPLETED);
        }

        /// <summary>
        /// Fill Parameter
        /// </summary>
        /// <param name="pInstance">process instance</param>
        /// <param name="trx">transaction</param>
        private void FillParameter(MPInstance pInstance, Trx trx)
        {
            GetPO(trx);
            //
            MWFNodePara[] nParams = _node.GetParameters();
            MPInstancePara[] iParams = pInstance.GetParameters();
            for (int pi = 0; pi < iParams.Length; pi++)
            {
                MPInstancePara iPara = iParams[pi];
                for (int np = 0; np < nParams.Length; np++)
                {
                    MWFNodePara nPara = nParams[np];
                    if (iPara.GetParameterName().Equals(nPara.GetAttributeName()))
                    {
                        String variableName = nPara.GetAttributeValue();
                        log.Fine(nPara.GetAttributeName() + " = " + variableName);
                        //	Value - Constant/Variable
                        Object value = variableName;



                        /////////
                        String columnName = "";
                        /////////
                        if (variableName == null || (variableName != null && variableName.Length == 0))
                            value = null;
                        else if (variableName.IndexOf("@") != -1 && _po != null)	//	we have a variable
                        {

                            //	Strip
                            int index = variableName.IndexOf("@");
                            if ((index + 1) < variableName.Length && variableName[index + 1].Equals('@'))
                            {
                                iPara.SetP_String(value.ToString());
                                try
                                {
                                    if (!iPara.Save())
                                    {
                                        log.Warning("Not Saved - " + nPara.GetAttributeName());
                                    }
                                }
                                catch { }
                                break;
                            }

                            columnName = variableName.Substring(index + 1);
                            index = columnName.IndexOf("@");
                            if (index == -1)
                            {
                                log.Warning(nPara.GetAttributeName() + " - cannot evaluate=" + variableName);
                                break;
                            }
                            //int startIndx = 0;
                            //int leng = (index - startIndx) + 1;//columnName.Length;
                            //columnName = columnName.Substring(startIndx, leng);

                            //raghu
                            columnName = columnName.Substring(0, index);
                            index = _po.Get_ColumnIndex(columnName);
                            if (index != -1)
                            {
                                value = _po.Get_Value(index);
                            }
                            else	//	not a column
                            {
                                //	try Env
                                String env = GetCtx().GetContext(columnName);
                                if (env.Length == 0)
                                {
                                    log.Warning(nPara.GetAttributeName() + " - not column nor environment =" + columnName + "(" + variableName + ")");
                                    break;
                                }
                                else
                                    value = env;
                            }
                        }	//	@variable@

                        //	No Value
                        if (value == null)
                        {
                            if (nPara.IsMandatory())
                                log.Warning(nPara.GetAttributeName() + " - empty - mandatory!");
                            else
                                log.Fine(nPara.GetAttributeName() + " - empty");
                            break;
                        }

                        //	Convert to Type
                        try
                        {
                            if (DisplayType.IsNumeric(nPara.GetDisplayType())
                                || DisplayType.IsID(nPara.GetDisplayType()))
                            {
                                Decimal bd;//= null;
                                if (value.GetType() == typeof(Decimal))
                                    bd = Convert.ToDecimal(value.ToString());
                                else if (value.GetType() == typeof(int))
                                    bd = Convert.ToDecimal(value.ToString());
                                else
                                    bd = Convert.ToDecimal(value.ToString());
                                iPara.SetP_Number(bd);
                                log.Fine(nPara.GetAttributeName() + " = " + variableName + " (=" + bd + "=)");
                            }
                            else if (DisplayType.IsDate(nPara.GetDisplayType()))
                            {
                                DateTime? ts;//= null;
                                if (value.GetType() == typeof(DateTime))
                                    ts = (DateTime?)value;
                                else
                                    ts = Convert.ToDateTime(value.ToString());
                                iPara.SetP_Date(ts);
                                log.Fine(nPara.GetAttributeName() + " = " + variableName + " (=" + ts + "=)");
                            }
                            else
                            {
                                /////////
                                //iPara.SetP_String(value.ToString());
                                variableName = variableName.Replace("@" + columnName + "@", value.ToString());
                                iPara.SetP_String(variableName);
                                ////////
                                //log.Fine(nPara.GetAttributeName() + " = " + variableName
                                //    + " (=" + value + "=) " + typeof(value).FullName);
                            }
                            if (!iPara.Save())
                            {
                                log.Warning("Not Saved - " + nPara.GetAttributeName());
                            }
                        }
                        catch (Exception e)
                        {
                            log.Warning(nPara.GetAttributeName()
                                + " = " + variableName + " (" + value
                                + ") " + value == null ?"null":value.GetType()
                                + " - " + e.Message);
                        }
                        break;
                    }
                }	//	node parameter loop
            }	//	instance parameter loop
        }

        /// <summary>
        /// Post Immediate
        /// </summary>
        private void PostImmediate()
        {
            //if (CConnection.get().isAppsServerOK(false))
            //{
            //    try
            //    {
            //        Server server = CConnection.get().getServer();
            //        if (server != null)
            //        {
            //            String error = server.PostImmediate(Env.getCtx(),
            //                _postImmediate.getAD_Client_ID(),
            //                _postImmediate.get_Table_ID(), _postImmediate.get_ID(),
            //                true, null);
            //            _postImmediate.get_Logger().config("Server: " + error == null ? "OK" : error);
            //            return;
            //        }
            //        else
            //            _postImmediate.get_Logger().config("NoAppsServer");
            //    }
            //    catch (RemoteException e)
            //    {
            //        _postImmediate.get_Logger().config("(RE) " + e.getMessage());
            //    }
            //    catch (Exception e)
            //    {
            //        _postImmediate.get_Logger().config("(ex) " + e.getMessage());
            //    }
            //}
        }

        /// <summary>
        /// Send EMail
        /// </summary>
        private void SendEMail(string action)
        {
            DocAction doc = null;
            bool isPOAsDocAction = false;

            if (_po is DocAction) //MClass Implement DocAction
            {
                doc = (DocAction)_po;
                isPOAsDocAction = true;
            }

            MMailText text = new MMailText(GetCtx(), _node.GetR_MailText_ID(), null);
            text.SetPO(_po, true); //Set _Po Current value

            int tableID = _po.Get_Table_ID();
            int RecID = _po.Get_ID();
            bool isHTML = text.IsHtml();

            String subject = "";
            if (isPOAsDocAction)
            {
                subject += doc.GetDocumentInfo();
            }
            else
            {
                subject += GetNode().GetDescription();
            }
            subject += ": " + text.GetMailHeader();

            String message = text.GetMailText(true);
            if (isPOAsDocAction)
            {
                message += "\n-----\n" + doc.GetDocumentInfo()
                 + "\n" + doc.GetSummary();
            }
            else
            {
                message += "\n-----\n" + GetNodeHelp();
            }

            FileInfo pdf = null;
            if (isPOAsDocAction)
            {
                pdf = doc.CreatePDF();
            }

            //byte[] data = null;
            //if (pdf != null)
            //{
            //    Stream stream = pdf.OpenRead();
            //    data = new byte[stream.Length];
            //    stream.Read(data, 0, data.Length);
            //}


            //
            MClient client = MClient.Get(_po.GetCtx(), isPOAsDocAction ? doc.GetAD_Client_ID() : _po.GetAD_Client_ID());

            //	Explicit EMail

            string nodeEmails = _node.GetEMail() ?? "";
            if (nodeEmails.IndexOf("@EMail@") != -1) //Get from PO
            {
                nodeEmails = nodeEmails.Replace("@EMail@", ParseVariable("EMail", _po));
            }


            //	Explicit EMail
            SendEMail(client, 0, nodeEmails, subject, message, pdf, isHTML, tableID, RecID,action);
            //	Recipient Type
            String recipient = _node.GetEMailRecipient();
            //	email to document user
            if (recipient == null || recipient.Length == 0)
            {
                if (isPOAsDocAction)
                {
                    SendEMail(client, doc.GetDoc_User_ID(), null, subject, message, pdf, isHTML, tableID, RecID, action);
                }
                else
                {
                    //client.SendEMail(to, GetNode().GetName(), subject, message, null);
                }
            }

            else if (recipient.Equals(MWFNode.EMAILRECIPIENT_DocumentBusinessPartner))
            {
                int bpID = 0, AD_User_ID = 0;
                if (_node.GetAD_Column_ID_1() > 0) //get c_Bpparner column id
                {
                    string colName = MColumn.Get(_po.GetCtx(), _node.GetAD_Column_ID_1()).GetColumnName(); //Get binded column name

                    Object oo = _po.Get_Value(_po.Get_ColumnIndex(colName));  // Getvalue of binded column from PO
                    if (oo is int)
                    {
                        bpID = int.Parse(oo.ToString()); //  Bussiness parnet id
                    }
                }


                int index = _po.Get_ColumnIndex("AD_User_ID"); //GetUserID
                if (index > -1)
                {
                    Object oo = _po.Get_Value(index);
                    if (oo is int)
                    {
                        AD_User_ID = int.Parse(oo.ToString());
                        //if (AD_User_ID != 0)
                        //{
                        // SendEMail(client, AD_User_ID, null, subject, message, data, isHTML, tableID, RecID);
                        // }
                        // else
                        // {
                        //    log.Fine("No User in Document");
                        // }
                    }
                    else
                    {
                        log.Fine("Empty User in Document");
                    }
                }
                else
                {
                    log.Fine("No User Field in Document");
                }
                if (bpID > 0) // if user binding Bpartner column id
                {
                    //Check User is member of Bussiness partnet
                    if (AD_User_ID > 0 && Convert.ToInt16(DB.ExecuteScalar("SELECT Count(AD_User_ID) FROM AD_User WHERE C_BPartner_ID = " + bpID + " AND AD_User_ID = " + AD_User_ID)) > 0)
                    {
                        SendEMail(client, AD_User_ID, null, subject, message, pdf, isHTML, tableID, RecID, action);
                    }
                    else // send to all user against Bpartner
                    {
                        DataSet ds = DB.ExecuteDataset("SELECT AD_User_ID FROM AD_User WHERE C_BPartner_ID =" + bpID, null);
                        foreach (DataRow dr in ds.Tables[0].Rows)
                        {
                            SendEMail(client, Convert.ToInt32(dr[0]), null, subject, message, pdf, isHTML, tableID, RecID, action);
                        }
                    }
                }
                else
                {
                    if (AD_User_ID > 0) //send email to user "AD_User_ID" column of record
                    {
                        SendEMail(client, AD_User_ID, null, subject, message, pdf, isHTML, tableID, RecID, action);
                    }
                    else
                    {
                        log.Fine("No User Field in Document");
                    }
                }
            }

            else if (recipient.Equals(MWFNode.EMAILRECIPIENT_DocumentUser)) // new Addition
            {
                if (_node.GetAD_Column_ID_2() > 0) //Node's User column
                {
                    string colName = MColumn.GetColumnName(_po.GetCtx(), _node.GetAD_Column_ID_2()); //get binded user column name

                    int index = _po.Get_ColumnIndex(colName); //get index of column

                    //index = index < 0 ? _po.Get_ColumnIndex("AD_User_ID") : index; //do not send to default User Column
                    if (index > -1) //if found
                    {
                        Object oo = _po.Get_Value(index); //Get value form record
                        if (oo is int)
                        {
                            int AD_User_ID = int.Parse(oo.ToString());
                            if (AD_User_ID != 0)
                            {
                                SendEMail(client, AD_User_ID, null, subject, message, pdf, isHTML, tableID, RecID, action);
                            }
                            else
                            {
                                log.Fine("No User in Document");
                            }
                        }
                        else
                        {
                            log.Fine("Empty User in Document");
                        }
                    }
                    else
                    {
                        log.Fine("No User Field in Document");
                    }
                }
                else
                {
                    log.Fine("No User Field in Document");
                }
            }



            else if (recipient.Equals(MWFNode.EMAILRECIPIENT_DocumentOwner))
            {
                if (doc != null)
                {
                    SendEMail(client, doc.GetDoc_User_ID(), null, subject, message, pdf, isHTML, tableID, RecID, action);
                }
                else
                {
                    SendEMail(client, Util.GetValueOfInt(_po.Get_Value("SalesRep_ID")), null, subject, message, pdf, isHTML, tableID, RecID, action);
                }
            }
            else if (recipient.Equals(MWFNode.EMAILRECIPIENT_WFResponsible))
            {
                MWFResponsible resp = GetResponsible();
                if (resp.IsInvoker())
                    SendEMail(client, doc.GetDoc_User_ID(), null, subject, message, pdf, isHTML, tableID, RecID, action);
                else if (resp.IsHuman())
                    SendEMail(client, resp.GetAD_User_ID(), null, subject, message, pdf, isHTML, tableID, RecID, action);
                else if (resp.IsRole())
                {
                    MRole role = resp.GetRole();
                    if (role != null)
                    {
                        MUser[] users = MUser.GetWithRole(role);
                        for (int i = 0; i < users.Length; i++)
                            SendEMail(client, users[i].GetAD_User_ID(), null, subject, message, pdf, isHTML, tableID, RecID, action);
                    }
                }
                else if (resp.IsOrganization())
                {
                    MOrgInfo org = MOrgInfo.Get(GetCtx(), _po.GetAD_Org_ID(), null);
                    if (org.GetSupervisor_ID() == 0)
                    {
                        log.Fine("No Supervisor for AD_Org_ID=" + _po.GetAD_Org_ID());
                    }
                    else
                        SendEMail(client, org.GetSupervisor_ID(), null, subject, message, pdf, isHTML, tableID, RecID, action);
                }
            }

            else if (recipient.Equals(MWFNode.EMAILRECIPIENT_DocumentOwnerSSupervisor))
            {
                if (isPOAsDocAction)
                {
                    int userID = Util.GetValueOfInt(DB.ExecuteScalar("SELECT supervisor_id FROM AD_User WHERE AD_User_ID=" + doc.GetDoc_User_ID()));
                    SendEMail(client, userID, null, subject, message, pdf, isHTML, tableID, RecID, action);
                }
            }



            //Send Emial To users binded on recipient tabs
            List<int> recipentUsers = GetRecipientUser();
            if (recipentUsers != null && recipentUsers.Count > 0 && action.Equals(MWFNode.ACTION_EMailPlusFaxEMail))
            {
                for (int i = 0; i < recipentUsers.Count; i++)
                {
                    SendEMail(client, recipentUsers[i], null, subject, message, pdf, isHTML, tableID, RecID, action);
                }
            }
        }



        private String ParseVariable(String variable, PO po)
        {
            int index = po.Get_ColumnIndex(variable);
            if (index == -1)
                return "";
            //
            Object value = po.Get_Value(index);
            if (value == null)
                return "";
            return value.ToString();
        }


        /// <summary>
        /// Send actual EMail
        /// </summary>
        /// <param name="client">client</param>
        /// <param name="AD_User_ID">user</param>
        /// <param name="email">email string</param>
        /// <param name="subject">subject</param>
        /// <param name="message">message</param>
        /// <param name="pdf">attachment</param>
        //private void SendEMail(MClient client, int AD_User_ID, String email, String subject,
        //    String message, FileInfo pdf)
        //{
        //    if (AD_User_ID != 0)
        //    {
        //        MUser user = MUser.Get(GetCtx(), AD_User_ID);
        //        email = user.GetEMail();
        //        if (email != null && email.Length > 0)
        //        {
        //            email = email.Trim();
        //            if (!_emails.Contains(email))
        //            {
        //                client.SendEMail(null, user, subject, message, pdf);
        //                _emails.Add(email);
        //            }
        //        }
        //        else
        //        {
        //            log.Info("No EMail for User " + user.GetName());
        //        }
        //    }
        //    else if (email != null && email.Length > 0)
        //    {
        //        //	Just one
        //        if (email.IndexOf(";") == -1)
        //        {
        //            email = email.Trim();
        //            if (!_emails.Contains(email))
        //            {
        //                client.SendEMail(email, null, subject, message, pdf);
        //                _emails.Add(email);
        //            }
        //            return;
        //        }
        //        //	Multiple EMail
        //        StringTokenizer st = new StringTokenizer(email, ";");
        //        while (st.HasMoreTokens())
        //        {
        //            String email1 = st.NextToken().Trim();
        //            if (email1.Length == 0)
        //                continue;
        //            if (!_emails.Contains(email1))
        //            {
        //                client.SendEMail(email1, null, subject, message, pdf);
        //                _emails.Add(email1);
        //            }
        //        }
        //    }
        //}



        /// <summary>
        /// Send actual EMail
        /// </summary>
        /// <param name="client"></param>
        /// <param name="AD_User_ID"></param>
        /// <param name="email"></param>
        /// <param name="subject"></param>
        /// <param name="message"></param>
        /// <param name="pdf"></param>
        /// <param name="isHTML"></param>
        private void SendEMail(MClient client, int AD_User_ID, String email, String subject,
            String message, FileInfo pdf, bool isHTML, int AD_Table_ID, int record_ID,string action, byte[] bArray = null)
        {
            if (AD_User_ID != 0)
            {
                MUser user = MUser.Get(GetCtx(), AD_User_ID);

                //Notice
                if (action != null && action.Equals(MWFNode.ACTION_EMailPlusFaxEMail) &&
                  (MUser.NOTIFICATIONTYPE_Notice.Equals(user.GetNotificationType()) ||
                   MUser.NOTIFICATIONTYPE_EMailPlusNotice.Equals(user.GetNotificationType()) ||
                   MUser.NOTIFICATIONTYPE_EMailPlusFaxEMail.Equals(user.GetNotificationType())))
                {
                    //Send Notice
                    SendNotice(AD_User_ID, message, subject);
                }

                email = user.GetEMail();

                if (user.IsEmail())
                {
                    ;
                }
                else if (!(MUser.NOTIFICATIONTYPE_EMail.Equals(user.GetNotificationType()) ||
                    MUser.NOTIFICATIONTYPE_EMailPlusFaxEMail.Equals(user.GetNotificationType())||
                    MUser.NOTIFICATIONTYPE_EMailPlusNotice.Equals(user.GetNotificationType())))
                {
                    return;
                }



                if (email == null)
                {
                    return;
                }
                //if (email != null && email.Length > 0)
                //{
                //    email = email.Trim();
                //    if (!_emails.Contains(email))
                //    {
                //        client.SendEMail(null, user, subject, message, pdf, isHTML);
                //        _emails.Add(email);
                //    }
                //}
                //else
                //{
                //    log.Info("No EMail for User " + user.GetName());
                //}
                if (email.IndexOf(";") == -1)
                {
                    email = email.Trim();
                    if (!_emails.Contains(email))
                    {
                        if (isDocxFile)
                        {
                            client.SendEMail(email, null, subject, message, pdf, isHTML, AD_Table_ID, record_ID, bArray, DateTime.Now.Millisecond.ToString() + bArray.Length + ".docx");
                        }
                        else
                        {
                            client.SendEMail(email, null, subject, message, pdf, isHTML, AD_Table_ID, record_ID, bArray);
                        }
                        _emails.Add(email);
                        return;
                    }
                    //return ;
                }
                //	Multiple EMail
                StringTokenizer st = new StringTokenizer(email, ";");
                while (st.HasMoreTokens())
                {
                    String email1 = st.NextToken().Trim();
                    if (email1.Length == 0)
                        continue;
                    if (!_emails.Contains(email1))
                    {
                        if (isDocxFile)
                        {
                            //DateTime.Now.Millisecond.ToString() + bArray.Length + ".docx"
                            client.SendEMail(email1, null, subject, message, pdf, isHTML, AD_Table_ID, record_ID, bArray, DateTime.Now.Millisecond.ToString() + bArray.Length + ".docx");
                        }
                        else
                        {
                            client.SendEMail(email1, null, subject, message, pdf, isHTML, AD_Table_ID, record_ID, bArray);
                        }
                        _emails.Add(email1);
                    }
                }
            }
            else if (email != null && email.Length > 0)
            {
                //	Just one
                if (email.IndexOf(";") == -1)
                {
                    email = email.Trim();
                    if (!_emails.Contains(email))
                    {
                        if (isDocxFile)
                        {
                            //DateTime.Now.Millisecond.ToString() + bArray.Length + ".docx"
                            client.SendEMail(email, null, subject, message, pdf, isHTML, AD_Table_ID, record_ID, bArray, DateTime.Now.Millisecond.ToString() + bArray.Length + ".docx");
                        }
                        else
                        {
                            client.SendEMail(email, null, subject, message, pdf, isHTML, AD_Table_ID, record_ID, bArray);
                        }
                        _emails.Add(email);
                    }
                    return;
                }
                //	Multiple EMail
                StringTokenizer st = new StringTokenizer(email, ";");
                while (st.HasMoreTokens())
                {
                    String email1 = st.NextToken().Trim();
                    if (email1.Length == 0)
                        continue;
                    if (!_emails.Contains(email1))
                    {
                        if (isDocxFile)
                        {
                            //DateTime.Now.Millisecond.ToString() + bArray.Length + ".docx"
                            client.SendEMail(email1, null, subject, message, pdf, isHTML, AD_Table_ID, record_ID, bArray, DateTime.Now.Millisecond.ToString() + bArray.Length + ".docx");
                        }
                        else
                        {
                            client.SendEMail(email1, null, subject, message, pdf, isHTML, AD_Table_ID, record_ID, bArray);
                        }
                        _emails.Add(email1);
                    }
                }
            }
        }


        private void SendNotice(int AD_User_ID, string message, string subject)
        {
            //	Create Notice
            MNote note = new MNote(GetCtx(), 0, Get_TrxName());
            note.SetAD_User_ID(AD_User_ID);
            note.SetClientOrg(GetAD_Client_ID(), GetAD_Org_ID());
            note.SetTextMsg(subject);
            note.SetDescription(message);
            note.SetRecord(Table_ID, Get_ID());		//	point to this
            note.SetAD_Message_ID(859);//Workflow
            note.Save(Get_TrxName());
        }

        /// <summary>
        /// Get Process Activity (Event) History
        /// </summary>
        /// <returns>history</returns>
        public String GetHistoryHTML()
        {
            StringBuilder sb = new StringBuilder();
            MWFEventAudit[] events = MWFEventAudit.Get(GetCtx(), GetAD_WF_Process_ID());
            //SimpleDateFormat format = DisplayType.getDateFormat(DisplayType.DateTime);
            for (int i = 0; i < events.Length; i++)
            {
                MWFEventAudit audit = events[i];
                sb.Append("<p style=\"width:90%\">");
                //sb.Append("<p>");
                //sb.Append(format.format(audit.GetCreated()))
                sb.Append(audit.GetCreated().ToLongDateString())
                    .Append(" ")
                    .Append(GetHTMLpart("b", audit.GetNodeName()))
                    .Append(": ")
                    .Append(GetHTMLpart(null, audit.GetDescription()))
                    .Append(GetHTMLpart("i", audit.GetTextMsg()));
                sb.Append("</p>");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Get HTML part
        /// </summary>
        /// <param name="tag">HTML tag</param>
        /// <param name="content">content</param>
        /// <returns><tag>content</tag></returns>
        private StringBuilder GetHTMLpart(String tag, String content)
        {
            StringBuilder sb = new StringBuilder();
            if (content == null || content.Length == 0)
                return sb;
            if (tag != null && tag.Length > 0)
                sb.Append("<").Append(tag).Append(">");
            sb.Append(content);
            if (tag != null && tag.Length > 0)
                sb.Append("</").Append(tag).Append(">");
            return sb;
        }

        /// <summary>
        /// Does the underlying PO (!) object have a PDF Attachment
        /// </summary>
        /// <returns>true if there is a pdf attachment</returns>
        public new bool IsPdfAttachment()
        {
            if (GetPO() == null)
                return false;
            return _po.IsPdfAttachment();
        }

        /// <summary>
        /// Get PDF Attachment of underlying PO (!) object
        /// </summary>
        /// <returns>pdf data or null</returns>
        public new byte[] GetPdfAttachment()
        {
            if (GetPO() == null)
                return null;
            return _po.GetPdfAttachment();
        }

        /// <summary>
        /// String Representation
        /// </summary>
        /// <returns>info</returns>
        public override String ToString()
        {
            StringBuilder sb = new StringBuilder("MWFActivity[");
            sb.Append(Get_ID()).Append(",Node=");
            if (_node == null)
                sb.Append(GetAD_WF_Node_ID());
            else
                sb.Append(_node.GetName());
            sb.Append(",State=").Append(GetWFState())
                .Append(",AD_User_ID=").Append(GetAD_User_ID())
                .Append(",").Append(GetCreated())
                .Append("]");
            return sb.ToString();
        }

        /// <summary>
        /// User String Representation.
        /// </summary>
        /// <returns></returns>
        public String ToStringX()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(GetWFStateText()).Append(": ").Append(GetNode().GetName());
            if (GetAD_User_ID() > 0)
            {
                MUser user = MUser.Get(GetCtx(), GetAD_User_ID());
                sb.Append(" (").Append(user.GetName()).Append(")");
            }
            return sb.ToString();
        }








        /// <summary>
        /// Send EMail
        /// </summary>
        private void SendFaxEMail()
        {
            DocAction doc = (DocAction)_po;
            MMailText text = new MMailText(GetCtx(), _node.GetR_MailText_ID(), null);
            text.SetPO(_po, true);
            bool isHTML = text.IsHtml();
            //
            String subject = doc.GetDocumentInfo()
                + ": " + text.GetMailHeader();
            String message = text.GetMailText(true)
                + "\n-----\n" + doc.GetDocumentInfo()
                + "\n" + doc.GetSummary();
            FileInfo pdf = doc.CreatePDF();
            //
            MClient client = MClient.Get(_po.GetCtx(), doc.GetAD_Client_ID());

            //	Explicit EMail
            SendFaxEMail(client, 0, _node.GetEMail(), subject, message, pdf, isHTML);
            //	Recipient Type
            String recipient = _node.GetEMailRecipient();
            //	email to document user
            if (recipient == null || recipient.Length == 0)
                SendFaxEMail(client, doc.GetDoc_User_ID(), null, subject, message, pdf, isHTML);
            else if (recipient.Equals(MWFNode.EMAILRECIPIENT_DocumentBusinessPartner))
            {
                int index = _po.Get_ColumnIndex("AD_User_ID");
                if (index > 0)
                {
                    Object oo = _po.Get_Value(index);
                    if (oo.GetType() == typeof(int))
                    {
                        int AD_User_ID = int.Parse(oo.ToString());
                        if (AD_User_ID != 0)
                        {
                            SendFaxEMail(client, AD_User_ID, null, subject, message, pdf, isHTML);
                        }
                        else
                        {
                            log.Fine("No User in Document");
                        }
                    }
                    else
                    {
                        log.Fine("Empty User in Document");
                    }
                }
                else
                {
                    log.Fine("No User Field in Document");
                }
            }
            else if (recipient.Equals(MWFNode.EMAILRECIPIENT_DocumentOwner))
            {
                if (doc != null)
                {
                    SendFaxEMail(client, doc.GetDoc_User_ID(), null, subject, message, pdf, isHTML);
                }
                else
                {
                    SendFaxEMail(client, Util.GetValueOfInt(_po.Get_Value("SalesRep_ID")), null, subject, message, pdf, isHTML);
                }
            }
            else if (recipient.Equals(MWFNode.EMAILRECIPIENT_WFResponsible))
            {
                MWFResponsible resp = GetResponsible();
                if (resp.IsInvoker())
                    SendFaxEMail(client, doc.GetDoc_User_ID(), null, subject, message, pdf, isHTML);
                else if (resp.IsHuman())
                    SendFaxEMail(client, resp.GetAD_User_ID(), null, subject, message, pdf, isHTML);
                else if (resp.IsRole())
                {
                    MRole role = resp.GetRole();
                    if (role != null)
                    {
                        MUser[] users = MUser.GetWithRole(role);
                        for (int i = 0; i < users.Length; i++)
                            SendFaxEMail(client, users[i].GetAD_User_ID(), null, subject, message, pdf, isHTML);
                    }
                }
                else if (resp.IsOrganization())
                {
                    MOrgInfo org = MOrgInfo.Get(GetCtx(), _po.GetAD_Org_ID(), null);
                    if (org.GetSupervisor_ID() == 0)
                    {
                        log.Fine("No Supervisor for AD_Org_ID=" + _po.GetAD_Org_ID());
                    }
                    else
                        SendFaxEMail(client, org.GetSupervisor_ID(), null, subject, message, pdf, isHTML);
                }
            }
            else if (recipient.Equals(MWFNode.EMAILRECIPIENT_DocumentOwnerSSupervisor))
            {

                int userID = Util.GetValueOfInt(DB.ExecuteScalar("SELECT supervisor_id FROM AD_User WHERE AD_User_ID=" + doc.GetDoc_User_ID()));
                SendFaxEMail(client, userID, null, subject, message, pdf, isHTML);

            }
        }

        /// <summary>
        /// Send actual EMail
        /// </summary>
        /// <param name="client">client</param>
        /// <param name="AD_User_ID">user</param>
        /// <param name="email">email string</param>
        /// <param name="subject">subject</param>
        /// <param name="message">message</param>
        /// <param name="pdf">attachment</param>
        private void SendFaxEMail(MClient client, int AD_User_ID, String email, String subject,
            String message, FileInfo pdf, bool isHTML)
        {
            if (AD_User_ID != 0)
            {
                MUser user = MUser.Get(GetCtx(), AD_User_ID);

                ////Notice
                //if (action != null && action.Equals(MWFNode.ACTION_EMailPlusFaxEMail) && MUser.NOTIFICATIONTYPE_Notice.Equals(user.GetNotificationType()))
                //{
                //    //Send Notice
                //    SendNotice(AD_User_ID, message, subject);
                //}

                //email = user.GetEMail();
                if (!(MUser.NOTIFICATIONTYPE_FaxEMail.Equals(user.GetNotificationType()) ||
                    MUser.NOTIFICATIONTYPE_EMailPlusFaxEMail.Equals(user.GetNotificationType())))
                {
                    return;
                }

                email = user.GetFaxEMail();
                if (email == null)
                {
                    return;
                }
                string faxEmailDomain = user.GetFaxEMailDomain();
                string ccode = user.GetCountryCodeForMobile();
                //if (email != null && email.Length > 0)
                //{
                //    email = email.Trim();
                //    if (!_emails.Contains(email))
                //    {
                //        client.SendEMail(null, user, subject, message, pdf);
                //        _emails.Add(email);
                //    }
                //}
                //else
                //{
                //    log.Info("No EMail for User " + user.GetName());
                //}
                if (email.IndexOf(";") == -1)
                {
                    email = email.Trim();
                    if (!_emails.Contains(email))
                    {
                        if (!String.IsNullOrEmpty(faxEmailDomain))
                        {
                            email += faxEmailDomain;
                        }
                        if (!string.IsNullOrEmpty(ccode))
                        {
                            email = ccode.Trim() + email;
                        }
                        client.SendEMail(email, null, subject, message, pdf, isHTML);
                        _emails.Add(email);
                    }
                    return;
                }
                //	Multiple EMail
                StringTokenizer st = new StringTokenizer(email, ";");
                while (st.HasMoreTokens())
                {
                    String email1 = st.NextToken().Trim();
                    if (email1.Length == 0)
                        continue;
                    if (!_emails.Contains(email1))
                    {
                        if (!String.IsNullOrEmpty(faxEmailDomain))
                        {
                            email1 += faxEmailDomain;
                        }
                        if (!string.IsNullOrEmpty(ccode))
                        {
                            email1 = ccode.Trim() + email1;
                        }
                        client.SendEMail(email1, null, subject, message, pdf, isHTML);
                        _emails.Add(email1);
                    }
                }
            }
            //else if (email != null && email.Length > 0)
            //{
            //    //	Just one
            //    if (email.IndexOf(";") == -1)
            //    {
            //        email = email.Trim();
            //        if (!_emails.Contains(email))
            //        {
            //            client.SendEMail(email, null, subject, message, pdf);
            //            _emails.Add(email);
            //        }
            //        return;
            //    }
            //    //	Multiple EMail
            //    StringTokenizer st = new StringTokenizer(email, ";");
            //    while (st.HasMoreTokens())
            //    {
            //        String email1 = st.NextToken().Trim();
            //        if (email1.Length == 0)
            //            continue;
            //        if (!_emails.Contains(email1))
            //        {
            //            client.SendEMail(email1, null, subject, message, pdf);
            //            _emails.Add(email1);
            //        }
            //    }
            //}
        }

        void SendSms()
        {

            int index = _po.Get_ColumnIndex("AD_User_ID");
            if (index > 0)
            {
                Object oo = _po.Get_Value(index);
                if (oo.GetType() == typeof(int))
                {
                    int AD_User_ID = int.Parse(oo.ToString());
                    if (AD_User_ID != 0)
                    {
                        MUser user = MUser.Get(GetCtx(), AD_User_ID);
                        StringBuilder res = new StringBuilder();
                        //if (MUser.NOTIFICATIONTYPE_SMS.Equals(user.GetNotificationType()))
                        {
                            string mobile = user.GetMobile();
                            string code = user.GetCountryCodeForMobile();
                            if (mobile != null)
                            {
                                if (!String.IsNullOrEmpty(mobile.Trim()))
                                {
                                    string[] mnums = mobile.Split(';');
                                    DocAction doc = (DocAction)_po;
                                    MMailText text = new MMailText(GetCtx(), _node.GetR_MailText_ID(), null);
                                    text.SetPO(_po, true);
                                    //string msg = text.GetMailText(true) + "\n-----\n" + doc.GetDocumentInfo()
                                    //                    + "\n" + doc.GetSummary();
                                    string msg = text.GetMailText(true);
                                    res.Clear();
                                    for (int i = 0; i < mnums.Length; i++)
                                    {
                                        if (!string.IsNullOrEmpty(mnums[i]))
                                        {
                                            mnums[i] = mnums[i].Trim();
                                            if (mnums[i].StartsWith("0"))
                                            {
                                                mnums[i] = mnums[i].Substring(1);
                                            }
                                            if (!string.IsNullOrEmpty(code))
                                            {
                                                mnums[i] = code.Trim() + mnums[i];
                                            }
                                            //if (ValidateMobileNumber(mnums[i]))
                                            //{
                                            if (!string.IsNullOrEmpty(mnums[i]))
                                            {
                                                res.Append(mnums[i] + "->").Append(SendSms(mnums[i], msg));
                                            }
                                            //}
                                            //else
                                            //{
                                            //    res.Append(mnums[i] + "->").Append("WrongMobileNumber");
                                            //}

                                        }

                                    }

                                }
                            }
                            SetTextMsg(res.ToString());
                        }
                    }
                    else
                    {
                        log.Fine("No User in Document");
                    }
                }
                else
                {
                    log.Fine("Empty User in Document");
                }
            }
            else
            {
                log.Fine("No User Field in Document");
            }

        }


        public string SendSms(string mbNumber, string msg)
        {


            //StringBuilder result = new StringBuilder("");

            IDataReader idr = null;
            idr = VAdvantage.DataBase.DB.ExecuteReader("select * from ad_smsconfiguration WHERE isactive='Y'");
            DataTable dt = new DataTable();
            dt.Load(idr);
            idr.Close();
            if (dt.Rows.Count == 0)
            {
                return "SmsConfigurationNotFound";
                //return false;
            }


            string strUrl = dt.Rows[0]["url"].ToString() + "?" + dt.Rows[0]["userKeyword"].ToString() + "=" +
                     dt.Rows[0]["username"].ToString() + "&" + dt.Rows[0]["PasswordKeyword"].ToString() + "=" +
                     dt.Rows[0]["password"].ToString() + "&" +
                     dt.Rows[0]["senderKeyword"].ToString() + "=" + dt.Rows[0]["sender"].ToString() + "&" +
                     dt.Rows[0]["messageKeyword"].ToString() + "=" +
                     "@@Messag@@" + "&" + dt.Rows[0]["MobilenumberKeyword"].ToString() + "=" + "@@Mobile@@" + "&";
            strUrl += dt.Rows[0]["priorityKeyword"].ToString() + "=" + dt.Rows[0]["priorityValue"].ToString();
            if (dt.Rows[0]["unicodeValue"].ToString().Length > 0 || dt.Rows[0]["dndValue"].ToString().Length > 0)
            {
                strUrl += "&" + dt.Rows[0]["dndKeyword"].ToString() + "=" + dt.Rows[0]["dndValue"].ToString() +
                    "&" + dt.Rows[0]["unicodeKeyword"].ToString() + "=" + dt.Rows[0]["unicodeValue"].ToString();
            }


            String resultmsg = string.Empty;
            try
            {
                string strRep = Replace(msg);
                string repMob = Replace(mbNumber);
                string uri = strUrl.Replace("@@Messag@@", strRep);
                uri = uri.Replace("@@Mobile@@", repMob);
                // uri += mbNumber;

                HttpWebRequest webReq = (HttpWebRequest)WebRequest.Create(uri);
                HttpWebResponse resp = (HttpWebResponse)webReq.GetResponse();//send sms
                StreamReader responseReader = new StreamReader(resp.GetResponseStream());//read the response 
                resultmsg = responseReader.ReadToEnd();//get result
                responseReader.Close();
                resp.Close();
                //  result.Append(resultmsg).Append(",");
            }
            catch (Exception e)
            {
                resultmsg = e.Message;
                //return false;
            }
            // return true;
            return resultmsg.ToString();
        }
        private string Replace(string str)
        {
            StringBuilder replaceSpecilChar = new StringBuilder();
            replaceSpecilChar.Append(str);
            // replaceSpecilChar.Replace("%", "%25");
            replaceSpecilChar.Replace("#", "%23");
            replaceSpecilChar.Replace("$", "%24");
            replaceSpecilChar.Replace("&", "%26");
            replaceSpecilChar.Replace("+", "%2B");
            replaceSpecilChar.Replace(",", "%2C");
            replaceSpecilChar.Replace(":", "%3A");
            replaceSpecilChar.Replace(";", "%3B");
            replaceSpecilChar.Replace("=", "%3D");
            replaceSpecilChar.Replace("?", "%3F");
            replaceSpecilChar.Replace("@", "%40");
            replaceSpecilChar.Replace("<", "%3C");
            replaceSpecilChar.Replace(">", "%3E");
            replaceSpecilChar.Replace("{", "%7B");
            replaceSpecilChar.Replace("}", "%7D");
            replaceSpecilChar.Replace("|", "%7C");
            replaceSpecilChar.Replace("\\", "%5C");
            replaceSpecilChar.Replace("^", "%5E");
            replaceSpecilChar.Replace("~", "%7E");
            replaceSpecilChar.Replace("[", "%5B");
            replaceSpecilChar.Replace("]", "%5D");
            replaceSpecilChar.Replace("`", "%60");

            return replaceSpecilChar.ToString();
        }


        private bool ValidateMobileNumber(String MobileNum)
        {
            bool RtnVal = true;
            //if (MobileNum.Contains("("))
            //{
            //    MobileNum = MobileNum.Substring(0, MobileNum.ToString().IndexOf("("));
            //}

            ////Length must be >= 10 but <= 12
            if ((MobileNum.Length < 10) || (MobileNum.Length > 12))
            {
                RtnVal = false;
            }
            else
            {
                int Pos;
                int NumChars;

                NumChars = MobileNum.Length;


                //Check For Characters 
                for (Pos = 0; Pos < NumChars; Pos++)
                {

                    if (!Char.IsDigit(MobileNum[Pos]))
                    {
                        if (!char.IsWhiteSpace(MobileNum[Pos])) //Space is allowed
                        {
                            if ((MobileNum[Pos] != '(') && (MobileNum[Pos] != ')')) //( and ) is allowed
                            {
                                RtnVal = false;
                            }

                        }

                    }
                }//for

                //Check For Opening and Closing bracket

                //if (MobileNum.Contains('('))
                //{
                //    if (!MobileNum.Contains(')'))
                //    {
                //        RtnVal = false;
                //    }

                //}


            }//else
            return RtnVal;
        }


        private List<int> GetRecipientUser()
        {

            DataSet ds = DB.ExecuteDataset("SELECT AD_WF_Recipient_ID FROM AD_WF_Recipient WHERE IsActive='Y' AND AD_WF_Node_ID=" + _node.Get_ID());
            if (ds == null || ds.Tables[0].Rows.Count == 0)
            {
                return null;
            }
            List<int> recipientUser = new List<int>();
            X_AD_WF_Recipient wfRecipient = null;
            //MRole role = null;
            // MUserRoles userRole = null;
            DataSet dsUserRole = null;
            int userID = 0;
            int roleID = 0;
            for (int i = 0; i < ds.Tables[0].Rows.Count; i++)
            {
                wfRecipient = new X_AD_WF_Recipient(GetCtx(), Util.GetValueOfInt(ds.Tables[0].Rows[i]["AD_WF_Recipient_ID"]), Get_TrxName());
                userID = wfRecipient.GetAD_User_ID();
                if (userID > 0 && !(recipientUser.Contains(userID)))
                {
                    recipientUser.Add(userID);
                }
                roleID = wfRecipient.GetAD_Role_ID();
                if (roleID == 0)
                {
                    continue;
                }
                //role = new MRole(Env.GetCtx(), roleID, Get_TrxName());
                dsUserRole = DB.ExecuteDataset("SELECT AD_User_ID FROM AD_User_roles WHERE IsActive='Y' AND AD_Role_ID=" + roleID);
                if (dsUserRole == null || dsUserRole.Tables[0].Rows.Count == 0)
                {
                    continue;
                }
                for (int j = 0; j < dsUserRole.Tables[0].Rows.Count; j++)
                {
                    userID = Util.GetValueOfInt(dsUserRole.Tables[0].Rows[j]["AD_User_ID"]);
                    if (!(recipientUser.Contains(userID)))
                    {
                        recipientUser.Add(userID);
                    }
                }

            }
            return recipientUser;
        }
        private List<int> GetRecipientUserOnly()
        {

            DataSet ds = DB.ExecuteDataset("SELECT AD_WF_Recipient_ID FROM AD_WF_Recipient WHERE IsActive='Y' AND AD_WF_Node_ID=" + _node.Get_ID());
            if (ds == null || ds.Tables[0].Rows.Count == 0)
            {
                return null;
            }
            List<int> recipientUser = new List<int>();
            X_AD_WF_Recipient wfRecipient = null;
            //MRole role = null;
            // MUserRoles userRole = null;
            //DataSet dsUserRole = null;
            int userID = 0;
            //int roleID = 0;
            for (int i = 0; i < ds.Tables[0].Rows.Count; i++)
            {
                wfRecipient = new X_AD_WF_Recipient(GetCtx(), Util.GetValueOfInt(ds.Tables[0].Rows[i]["AD_WF_Recipient_ID"]), Get_TrxName());
                userID = wfRecipient.GetAD_User_ID();
                if (userID > 0 && !(recipientUser.Contains(userID)))
                {
                    recipientUser.Add(userID);
                }
            }
            return recipientUser;
        }
        private List<int> GetRecipientRoles()
        {

            DataSet ds = DB.ExecuteDataset("SELECT AD_WF_Recipient_ID FROM AD_WF_Recipient WHERE IsActive='Y' AND AD_WF_Node_ID=" + _node.Get_ID());
            if (ds == null || ds.Tables[0].Rows.Count == 0)
            {
                return null;
            }
            List<int> recipientRoles = new List<int>();
            X_AD_WF_Recipient wfRecipient = null;           
            int roleID = 0;
            for (int i = 0; i < ds.Tables[0].Rows.Count; i++)
            {
                wfRecipient = new X_AD_WF_Recipient(GetCtx(), Util.GetValueOfInt(ds.Tables[0].Rows[i]["AD_WF_Recipient_ID"]), Get_TrxName());
             
                roleID = wfRecipient.GetAD_Role_ID();
                if (roleID > 0 && !(recipientRoles.Contains(roleID)))
                {
                    recipientRoles.Add(roleID);
                }             
            }
            return recipientRoles;
        }
        private int GetUserFromWFResponsible(int AD_WF_Responsible_ID, PO po)
        {
            MWFResponsible resp = new MWFResponsible(GetCtx(), AD_WF_Responsible_ID, Get_TrxName());
            if (resp.GetResponsibleType().Equals(MWFResponsible.RESPONSIBLETYPE_Human))
            {
                return resp.GetAD_User_ID();
            }
            else if (resp.GetResponsibleType().Equals(MWFResponsible.RESPONSIBLETYPE_Organization))
            {
                return (new MOrgInfo(GetCtx(), Util.GetValueOfInt(po.Get_Value("AD_Org_ID")), Get_TrxName()).GetSupervisor_ID());
            }
            else if (resp.GetResponsibleType().Equals(MWFResponsible.RESPONSIBLETYPE_Role))
            {
                return Util.GetValueOfInt(DB.ExecuteScalar("select ad_user_ID from ad_user_roles where Ad_role_ID=" + resp.GetAD_Role_ID() + " AND isactive='Y'"));
            }
            else if (Util.GetValueOfInt(po.Get_Value("SalesRep_ID")) > 0)
            {
                return Util.GetValueOfInt(po.Get_Value("SalesRep_ID"));
            }
            else
            {
                return GetCtx().GetAD_User_ID();
            }
        }



        private string SaveActionLog(string emailto,Trx trxname)
        {
            string res =Util.GetValueOfString( DB.ExecuteScalar(@"SELECT
                              CASE
                                WHEN ( (SELECT AD_Table_ID FROM AD_Table WHERE TableName='VADMS_MetaData')=
                                  (SELECT AD_Table_ID
                                  FROM AD_WorkFlow
                                  WHERE IsActive    ='Y'
                                  AND AD_WorkFlow_ID=
                                    (SELECT AD_Workflow_ID FROM AD_WF_Node WHERE AD_Wf_Node_ID ="+GetAD_WF_Node_ID()+@"
                                    )
                                  ) )
                                THEN 'True'
                                ELSE 'False'
                              END AS Status
                            FROM Dual"));
            if (res == "False")//Save log Only In case of DMS Tables
            {
                return string.Empty;
            }

            DocumentAction action = new DocumentAction();
            int VADMS_Document_ID = Util.GetValueOfInt(DB.ExecuteScalar("SELECT VADMS_Document_ID From VADMS_Metadata WHERE IsActive='Y' AND VADMS_Metadata_ID="+GetRecord_ID()));

            action.SaveActionLog(GetCtx(), VADMS_Document_ID, GetCtx().GetAD_User_ID(), 0, Msg.GetMsg(GetCtx(),"VADMS_DoneBySystem"), emailto, trxname, out res);

            return res;
        }

        public String GetSummary()
        {
            PO po = GetPO();
            if (po == null)
                return null;
            StringBuilder sb = new StringBuilder();
            //String[] keyColumns = po.Get_POInfo().getKeyColumns();
            //if ((keyColumns != null) && (keyColumns.Length > 0))
            //    sb.Append(Msg.GetElement(GetCtx(), keyColumns[0])).Append(" ");
            int index = po.Get_ColumnIndex("DocumentNo");
            if (index != -1)
                sb.Append(po.Get_Value(index)).Append(": ");
            index = po.Get_ColumnIndex("SalesRep_ID");
            int? sr = null;
            if (index != -1)
                sr = (int?)po.Get_Value(index);
            else
            {
                index = po.Get_ColumnIndex("AD_User_ID");
                if (index != -1)
                    sr = (int?)po.Get_Value(index);
            }
            if (sr != null)
            {
                MUser user = MUser.Get(GetCtx(), sr.Value);
                if (user != null)
                    sb.Append(user.GetName()).Append(" ");
            }
            //
            index = po.Get_ColumnIndex("C_BPartner_ID");
            if (index != -1)
            {
                int? bp = (int?)po.Get_Value(index);
                if (bp != null)
                {
                    MBPartner partner = MBPartner.Get(GetCtx(), bp.Value);
                    if (partner != null)
                        sb.Append(partner.GetName()).Append(" ");
                }
            }
            return sb.ToString();
        }
    }
}
