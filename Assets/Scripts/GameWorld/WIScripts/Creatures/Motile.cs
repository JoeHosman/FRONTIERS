using UnityEngine;
using System;
using System.Xml;
using System.Xml.Serialization;
using System.Collections;
using System.Collections.Generic;
using Frontiers.World.Locations;
using Frontiers.World.Gameplay;
using Frontiers.Data;
using Pathfinding;
using Pathfinding.RVO;
using ExtensionMethods;
using Frontiers.GUI;

namespace Frontiers.World
{
		//TODO: Implement GetComponent<TNObject> ().isMine
		public class Motile : WIScript, IBodyOwner
		{
				public MotileState State = new MotileState();
				//Motile's job is to move the character's Goal around in ways that make sense
				//Motile also ensures that old actions are completed before new actions are undertaken
				//if there's a problem with character / creature movement
				//you'll find it in here
				[NObjectSync]//used to determine which options appear in option list
				public MotileInstructions CurrentInstructions {
						get {
								//update our motile instructions if we're the brain
								if (worlditem.IsNObject && worlditem.NObject.isMine) {
										if (State.Actions.Count > 0) {
												if (State.Actions[0].Instructions == MotileInstructions.InheritFromBase) {
														mMotileInstructions = State.BaseAction.Instructions;
												} else {
														mMotileInstructions = State.Actions[0].Instructions;
												}
										}
								}
								return mMotileInstructions;
						}
						set {
								mMotileInstructions = value;
						}
				}

				#region IBodyOwner implementation

				[NObjectSync]//used for stuff other than animation
				public double CurrentMovementSpeed {
						get {
								return mCurrentMovementSpeed;
						}
						set {
								mCurrentMovementSpeed = value;
						}
				}

				public double CurrentRotationSpeed {
						get {
								return mCurrentMovementSpeed;
						}
						set {
								mCurrentMovementSpeed = value;
						}
				}

				public double CurrentRotationChangeSpeed;

				public int CurrentIdleAnimation { get; set; }

				public Vector3 Position {
						get {
								return mPosition;
						}
						set {
								if (!enabled) {
										mTr.position = value;
								}
						}
				}

				public Quaternion Rotation { get { return mVisibleRotation; } }

				public WorldBody Body { get; set; }

				public override bool Initialized { get { return mInitialized; } }

				public bool IsImmobilized { get; set; }

				public bool IsGrounded { get; set; }

				public bool IsRagdoll { get; set; }

				#endregion

				public double TargetMovementSpeed = 0.0f;
				public Transform GoalObject = null;
				public Vector3 GoalDirection;
				public Vector3 LookDirection;
				public Vector3 ForceDirection;
				public float GoalDistance;

				public bool HasGoalHolder {
						get {
								return GoalHolder != null;
						}
				}

				public RVOTargetHolder GoalHolder = null;
				public float AdjustedYPosition = 0f;
				public RVOController rvoController;
				public ActionNode LastOccupiedNode = null;
				public float ThrowbackSpeed = 10f;

				public bool HasBody {
						get {
								//this is like motile's version of mInitialized
								//since we have to wait till post-OnInitialized to receive our body
								return Body != null;
						}
				}

				public bool HasReachedGoal(double minRange)
				{
						return GoalDistance < minRange;
				}

				#region initialization

				public override void Awake()
				{
						mTr = transform;
						base.Awake();
				}

				public override void OnStartup()
				{
						rvoController = gameObject.GetComponent <RVOController>();
						//just disable to the controller until we're ready
						rvoController.enabled = false;
						//set immobilized to true
						//this will prevent anything from moving before we're ready
						IsImmobilized = true;
						//create goal objects, focus objects etc.
						//we will only need these if we're the brain
						//put the goal object out in the world, not in the group
						GoalObject = new GameObject(worlditem.FileName + "-GoalObject").transform;
						State.BaseAction.State = MotileActionState.NotStarted;
						State.BaseAction.BaseAction = true;
				}

				public void SetBody(WorldBody body)
				{
						Body = body;
						//wait to actually spawn it until on added to group
						//that will zap the body to the correct position
				}

				public override void OnInitialized()
				{
						if (!HasBody) {
								Finish();
								return;
						}

						worlditem.OnAddedToGroup += OnAddedToGroup;
						//don't subscribe to on visible / active in here
						//because we don't want those messages until we have a body
						//and that won't happen until we've been added to a group
						State.BaseAction.BaseAction = true;
						State.BaseAction.HudIcon = Icon.Empty;
						State.BaseAction.Expiration = MotileExpiration.Never;
						State.BaseAction.Instructions = MotileInstructions.None;
				}

				public void OnAddedToGroup()
				{
						if (mFinished)
								return;

						if (worlditem.IsNObject && worlditem.NObject.isMine) {
								//worlditem.OnActive += OnActive;
								//worlditem.OnVisible += OnVisible;
								//worlditem.OnInactive += OnInactive;
								worlditem.OnInvisible += OnInvisible;
								//worlditem.OnPlayerEncounter += OnPlayerEncounter;

								TargetMovementSpeed = 0f;
								CurrentRotationChangeSpeed = 1f;

								IsGrounded = true;
								mPosition = mTr.position;
								AdjustedYPosition = mPosition.y;
								//make sure we have something to do
								GoalObject.position = mTr.position + (mTr.forward * 0.5f);

								terrainHit.overhangHeight = Globals.DefaultCharacterHeight;
								terrainHit.groundedHeight = Mathf.Max(Globals.DefaultCharacterGroundedHeight, State.MotileProps.GroundedHeight);
								terrainHit.isGrounded = true;
								terrainHit.hitTerrain = true;
								terrainHit.feetPosition = mPosition;

								Damageable damageable = null;
								if (worlditem.Is <Damageable>(out damageable)) {
										damageable.OnDie += OnDie;
										//we have to handle force on our own
										damageable.ApplyForceAutomatically = false;
										damageable.OnForceApplied += OnForceApplied;
								}

						} else {
								//initialize this motile script as a shell
								//it won't perform any actions on its own
								//but players will still be able to interact with it and give it commands
								//rvoController.canSearch = false;
								//TODO figure out what else to initialize
								//TODO figure out a way to send MotileActions across the network
						}
						//set immobilized to false and start updating everything
						IsImmobilized = false;
						//spawn the body so it will zap to our position
						Body.OnSpawn(this);
						//set the rvo controller's initial properties
						rvoController.InitializeController(State.MotileProps.RVORadius);
						//enable to start FixedUpdate
						//StartMotileActions ();
				}

				public void StartMotileActions()
				{
						if (mDestroyed || mFinished || !mInitialized)
								return;

						if (!mDoingActionsOverTime) {
								mDoingActionsOverTime = true;
								StartCoroutine(DoActionsOverTime());
						}

						enabled = true;
				}

				public void StopMotileActions()
				{
						//this will timeout on its own now
						mDoingActionsOverTime = false;

						while (mNewActions.Count > 0) {
								KeyValuePair <MotileActionPriority, MotileAction> newAction = mNewActions.Dequeue();
								newAction.Value.State = MotileActionState.Error;
								newAction.Value.Error = MotileActionError.MotileIsDead;
						}
						if (State.Actions.Count > 0) {
								for (int i = State.Actions.Count - 1; i >= 0; i--) {
										State.Actions[i].State = MotileActionState.Error;
										State.Actions[i].Error = MotileActionError.MotileIsDead;
								}
						}
						State.Actions.Clear();
						if (worlditem.Is(WIActiveState.Invisible)) {
								enabled = false;
						}
				}

				public override void OnEnable()
				{
						//make sure to keep this!
						base.OnEnable();

						if (!HasBody || !mInitialized)
								return;

						Body.SetVisible(true);
						Body.IgnoreCollisions(false);
						if (IsGrounded) {
								rvoController.SetEnabled(true);
						}
				}

				public void OnDisable()
				{
						if (!HasBody || !mInitialized)
								return;

						Body.SetVisible(false);
						Body.IgnoreCollisions(true);
						rvoController.SetEnabled(false);
				}

				public override void OnFinish()
				{
						if (rvoController != null) {
								rvoController.SetEnabled(false);
						}
						if (GoalObject != null) {
								GameObject.Destroy(GoalObject.gameObject);
						}
				}

				public void OnInvisible()
				{
						StopMotileActions();
				}

				#endregion

				public void OnForceApplied()
				{
						Damageable damageable = worlditem.Get <Damageable>();
						ForceDirection = damageable.State.LastDamageForce;
				}
				//what we're doing right now - the top item on the MotileAction 'stack'
				public MotileAction TopAction {
						get {
								if (State.Actions.Count > 0) {
										return State.Actions[0];//lowest to highest
								} else {
										State.BaseAction.BaseAction = true;//set this first
										return State.BaseAction;
								}
						}
				}

				public MotileAction BaseAction {
						get {
								return State.BaseAction;
						}
				}

				#region commands

				//do something! priority tells us whether to put this at the front or back of the action stack
				public bool PushMotileAction(MotileAction newAction, MotileActionPriority priority)
				{
						if (newAction.State != MotileActionState.NotStarted) {
								//interesting...
								return false;
						}

						if (worlditem.IsNObject && !worlditem.NObject.isMine) {
								//TODO send this motile action to the server somehow
								return true;
						}

						//otherwise handle it locally
						if (worlditem.Is(WIMode.RemovedFromGame)) {
								newAction.State = MotileActionState.Error;
								newAction.Error = MotileActionError.MotileIsDead;
								return false;
						}

						if (State.Actions.Contains(newAction)) {
								Debug.Log("Already contains action, not adding");
								return false;
						}

						//if it's our base action		
						newAction.WTAdded = WorldClock.Time;
						newAction.State = MotileActionState.NotStarted;
						newAction.BaseAction = false;//just in case

						if (!worlditem.Is(WILoadState.Initialized)) {
								//////Debug.Log ("Not initialized, putting in queue");
								mNewActions.Enqueue(new KeyValuePair <MotileActionPriority, MotileAction>(priority, newAction));
						} else {
								if (newAction == State.BaseAction) {
										//we can't add our own base action, dimwit
										return false;
								} else if (State.Actions.Count > 0) {//check and see if any of these actions are 'duplicate'
										for (int i = State.Actions.Count - 1; i >= 0; i--) {//if it's the same type and live target
												//treat it as the same action
												//remove the existing action and push the new action
												MotileAction existingAction = State.Actions[i];
												if (existingAction == newAction) {	//whoops! it's already in there
														return true;
												}
												//TODO figure out some sensible replacement rules
												//} else if (existingAction.Type == newAction.Type
												//&&	existingAction.LiveTarget	== newAction.LiveTarget) {
												//newAction.State = MotileActionState.Error;
												//newAction.Error = MotileActionError.Replaced;
												//existingAction.Expiration = newAction.Expiration;
												//existingAction.Target = newAction.Target;
												//existingAction.LiveTarget	= newAction.LiveTarget;
												//}
										}
								}
								mNewActions.Enqueue(new KeyValuePair <MotileActionPriority, MotileAction>(priority, newAction));
						}
						return true;
				}

				public void GoToActionNode(string actionNodeName)
				{
						WorldChunk chunk = worlditem.Group.GetParentChunk();
						ActionNodeState actionNodeState = null;
						if (chunk.GetNode(actionNodeName, true, out actionNodeState)) {
								if (actionNodeState.actionNode.TryToReserve(worlditem)) {
										StopMotileActions();
										MotileAction newAction = new MotileAction();
										//TEMP
										newAction.Method = MotileGoToMethod.StraightShot;
										newAction.Type = MotileActionType.GoToActionNode;
										newAction.Target = new MobileReference(actionNodeState.Name, actionNodeState.ParentGroupPath);
										//newAction.LiveTarget = actionNodeState.actionNode.gameObject;
										newAction.Expiration = MotileExpiration.Never;

										PushMotileAction(newAction, MotileActionPriority.ForceTop);
								}
						}
				}

				#endregion

				#region interaction / state changes

				public override void OnRefreshHud(WIHud hud)
				{
						//if (!TopAction.HudIcon.IsEmpty) {
						//GUIHudElement element = hud.GetOrAddElement (HudElementType.Icon, "MotileIcon");
						//element.Initialize (TopAction.HudIcon);
						//} else {
						//	hud.RemoveElement ("MotileIcon");
						//}

						//if (TopAction.Instructions == MotileInstructions.CompanionInstructions) {
						//	hud.GetPlayerAttention = true;
						//} else {
						//	hud.GetPlayerAttention = false;
						//}
				}

				public void OnLosePlayerAttention()
				{
						//if we're currently focusing on the player, finsih that action
						if (TopAction.Type == MotileActionType.FocusOnTarget
						    && TopAction.LiveTarget == Player.Local) {//tell it to finish externally
								TopAction.TryToFinish();
						}
				}

				public void OnGainPlayerAttention()
				{
						//create a 'focus on player' action and put it up top
						MotileAction newMotileAction = new MotileAction();
						newMotileAction.Type = MotileActionType.FocusOnTarget;
						newMotileAction.LiveTarget = Player.Local;
						newMotileAction.Target.FileName = "[Player]";
						newMotileAction.Expiration = MotileExpiration.Duration;
						newMotileAction.YieldBehavior = MotileYieldBehavior.YieldAndFinish;
						newMotileAction.RTDuration = 5.0f;
						//player attention is seldom less important than anything else
						//yield behavior will catch any exceptions to this
						PushMotileAction(newMotileAction, MotileActionPriority.ForceTop);
				}

				public override void PopulateOptionsList(List <GUIListOption> options, List <string> message)
				{
						//TODO get options list from server object?
						MotileAction topAction = TopAction;
						if (topAction.Instructions == MotileInstructions.None || !worlditem.HasPlayerAttention) {//nothing we can do here
								return;
						}

						GUIListOption followMeOption = null;
						GUIListOption waitHereOption = null;

						switch (CurrentInstructions) {
								case MotileInstructions.CompanionInstructions:
										if (topAction.Type == MotileActionType.FollowTargetHolder && topAction.LiveTarget == Player.Local) {
												waitHereOption = new GUIListOption("SkillIconGuildFollowTrail", "Stop Following", "Wait Here");
												waitHereOption.NegateIcon	= true;
												options.Add(waitHereOption);
										} else {
												followMeOption = new GUIListOption("SkillIconGuildFollowTrail", "Follow Me", "Follow Me");
												options.Add(followMeOption);
										}
										break;

								case MotileInstructions.PilgrimInstructions:
										waitHereOption = new GUIListOption("SkillIconGuildFollowTrail", "Wait Here", "Wait Here");
										waitHereOption.NegateIcon = true;
										options.Add(waitHereOption);
										break;

								default:
										break;
						}
				}

				public void OnPlayerUseWorldItemSecondary(object secondaryResult)
				{
						OptionsListDialogResult dialogResult = secondaryResult as OptionsListDialogResult;

						MotileAction action = null;
						switch (dialogResult.SecondaryResult) {
								case "Follow Me":
										action = new MotileAction();
										action.Type = MotileActionType.FollowTargetHolder;
										action.YieldBehavior = MotileYieldBehavior.YieldAndWait;
										action.FollowType = MotileFollowType.Follower;
										action.LiveTarget = Player.Local;
										action.Expiration = MotileExpiration.TargetOutOfRange;
										action.OutOfRange = 50.0f;
										action.Target.FileName	= "[Player]";
										break;

								case "Stop Following":
										if (TopAction.Type == MotileActionType.FollowTargetHolder
										    &&	TopAction.LiveTarget == Player.Local) {
												TopAction.TryToFinish();
										}
										break;

								case "Wait Here":
										action = new MotileAction();
										action.Type = MotileActionType.Wait;
										action.YieldBehavior	= MotileYieldBehavior.YieldAndWait;
										action.FollowType = MotileFollowType.Follower;
										action.LiveTarget = Player.Local;
										action.Expiration = MotileExpiration.Never;
										action.Target.FileName	= "[Player]";
										break;

								default:
										break;
						}

						if (action != null) {
								PushMotileAction(action, MotileActionPriority.ForceTop);
						}
				}

				public void OnDie()
				{
						StopMotileActions();
						IsRagdoll = true;
				}

				#endregion

				#region Update / Falling / Landing

				protected int mCheckTerrainHeight = 0;
				protected int mCheckTopActionAnimation = 0;

				public void Update()
				{
						if (!GameManager.Is(FGameState.InGame))//don't update while paused
							return;

						if (WorldClock.SkippingAhead)
								return;

						//if (worlditem.IsNObject && !worlditem.NObject.isMine)
						//return;

						if (!HasBody)//this shouldn't happen but whatever, just wait
							return;

						mVisibleRotation = rvoController.targetRotation;
				}

				int mCheckUpdate = 0;

				public void FixedUpdate()
				{
						mCheckUpdate++;
						if (mCheckUpdate < 5)
								return;

						mCheckUpdate = 0;

						if (!GameManager.Is(FGameState.InGame))//don't update while paused
							return;

						if (WorldClock.SkippingAhead)
								return;

						//if (worlditem.IsNObject && !worlditem.NObject.isMine)
						//return;

						if (!HasBody)//this shouldn't happen but whatever, just wait
							return;

						//get the latest position from our transform
						//set the feet position for our Y update
						mPosition = mTr.position;
						terrainHit.feetPosition = mPosition;

						if (IsImmobilized) {
								IsGrounded = true;
								mLastGroundedTime = WorldClock.Time;
								mLastGroundedHeight = mPosition.y;
								//setting this to false isn't overkill just do it every frame
								rvoController.SetEnabled(false);
								return;
						} else if (IsRagdoll) {
								//ragdoll prohibits rvo controller from working
								//grounded ceases to have any meaning here so don't bother to set it
								rvoController.SetEnabled(false);
								//update our position to the ragdoll's position
								//this and update fall are the only 2 places where we ever take direct control of our position
								//and this is the ONLY place where we take our position from the body & not the other way around
								mTr.position = Body.SmoothPosition;
								//don't bother with rotation
								return;
						} else if (IsGrounded) {
								//setting this to false isn't overkill just do it every frame
								rvoController.SetEnabled(true);
						}

						//if we're not immobilized
						//figure out our world elevation
						mCheckTerrainHeight++;
						if (mCheckTerrainHeight > 2) {
								mCheckTerrainHeight = 0;
								if (worlditem.Group.Props.Interior) {
										AdjustedYPosition = GameWorld.Get.InteriorHeightAtInGamePosition(ref terrainHit);
								} else {
										AdjustedYPosition = GameWorld.Get.TerrainHeightAtInGamePosition(ref terrainHit);
								}

								if (State.MotileProps.Hovers) {
										//update our adjusted y position over time to ease into our hover height
										//use max to ensure that you'll never be UNDER the terrain height
										AdjustedYPosition = Mathf.Max(AdjustedYPosition, Mathf.Lerp(mPosition.y, AdjustedYPosition + State.MotileProps.HoverHeight, (float)(State.MotileProps.HoverChangeSpeed * WorldClock.ARTDeltaTime)));
								}
						}

						mCheckTopActionAnimation++;
						if (mCheckTopActionAnimation > 3) {
								mCheckTopActionAnimation = 0;
								MotileAction topAction = TopAction;
								Body.Animator.IdleAnimation = topAction.IdleAnimation;
						}

						//update hovering / falling
						if (State.MotileProps.Hovers) {
								//hovering means we're always grounded
								//so no don't bother checking the ground position
								IsGrounded = true;
						} else {
								//if we weren't grounded before
								//and we are grounded now
								if (!IsGrounded) {
										if (terrainHit.isGrounded) {
												//yay, we weren't grounded but now we are, hit the ground
												StopFalling();
												//don't bother to update again this frame
												return;
										} else {
												//we're still not grounded, keep falling
												UpdateFalling();
												//don't bother to update direction
												return;
										}
										//if were grounded before
										//and we're not grounded now
								} else if (!terrainHit.isGrounded) {
										StartFalling();
										//don't bother to update direction
										return;
								}
						}
						//if force is greater than zero
						//we have to go in that direction
						if (ForceDirection != Vector3.zero) {
								CurrentMovementSpeed = ForceDirection.magnitude * ThrowbackSpeed;
								CurrentRotationSpeed = State.MotileProps.RotationChangeSpeed;
								rvoController.UpdateController(
										ForceDirection,
										CurrentMovementSpeed,
										CurrentRotationSpeed,
										AdjustedYPosition,
										mDesiredLookDirection);
								//fade out the force direction
								ForceDirection = Vector3.Lerp(ForceDirection, Vector3.zero, 0.5f);
						} else {
								//we're grounded now and we were before
								//so just move normally
								//update when we were last grounded
								mLastGroundedHeight = AdjustedYPosition;
								mLastGroundedTime = WorldClock.Time;
								//get the goal info
								//the direction is either to the goal object OR to the goal holder if we have one
								GoalDirection = (GoalObject.position - mPosition).normalized;
								LookDirection = GoalDirection;
								if (HasGoalHolder) {
										LookDirection = Vector3.Normalize(GoalHolder.tr.position - mPosition);
								}
								//the distance is always to the goal object
								GoalDistance = Vector3.Distance(GoalObject.position, mPosition);

								//figure out how fast we're going
								//target movement speed is set by our motile update scripts
								CurrentMovementSpeed = WorldClock.Lerp(CurrentMovementSpeed, TargetMovementSpeed, WorldClock.ARTDeltaTime * State.MotileProps.MovementChangeSpeed);

								//figure out our desired velocity
								mDesiredLookDirection = LookDirection;
								mDesiredDirection = GoalDirection;
								if (!HasReachedGoal(rvoController.radius)) {
										mDesiredDirection = GoalDirection;
								} else {
										mDesiredLookDirection = LookDirection;
										CurrentMovementSpeed = 0f;
								}

								//tell the controller to move and rotate us
								rvoController.UpdateController(
										mDesiredDirection,
										CurrentMovementSpeed,
										CurrentRotationChangeSpeed,
										AdjustedYPosition,
										mDesiredLookDirection);
								//figure out of we wamt to lock the rotation
						}
				}

				public void UpdateFalling()
				{
						double timeSinceFall = WorldClock.GameSecondsToRTSeconds(WorldClock.Time - mLastGroundedTime);
						mPosition.y -= (float)WorldClock.Lerp(0, Globals.DefaultCharacterFallAcceleration, timeSinceFall);
						//this and Ragdoll are the only two places where we update our position manually
						//because the RVO controller isn't doing it for us
						mTr.position = mPosition;
						//don't bother with rotation
				}

				public void StartFalling()
				{
						//we won't be needing our rvo sim while we fall
						//so deactivate it here
						rvoController.SetEnabled(false);
						IsGrounded = false;
						mLastGroundedHeight = AdjustedYPosition;
						mLastGroundedTime = WorldClock.Time;
				}

				public void StopFalling()
				{
						//TODO use last grounded height to apply damage, if applicable
						IsGrounded = true;
						mLastGroundedHeight = AdjustedYPosition;
						mLastGroundedTime = WorldClock.Time;
						//now that we've hit the ground
						//set the rvo controller to active again
						//this will automatically teleport it to our current position
						rvoController.SetEnabled(true);
				}

				#endregion

				#region motile actions

				public void TryToFinishMotileAction(MotileAction existingAction)
				{
						MotileAction topAction = TopAction;
						if (topAction.Type == existingAction.Type
						    && topAction.LiveTarget == existingAction.LiveTarget) {
								topAction.TryToFinish();
						}
				}

				protected IEnumerator DoActionsOverTime()
				{
						//give the world time to catch up with us
						while (!Initialized) {
								yield return null;
						}

						while (!mFinished && mDoingActionsOverTime) {

								//keep going until stop motile actions is called or the script is finished
								MotileAction topAction = TopAction;//keep a copy of the current top action
								//Debug.Log ("Got top action " + topAction.Name + " - is base action? " + topAction.BaseAction.ToString ());
								//worlditem.RefreshHud ();
								while (mNewActions.Count > 0) {
										//handle new actions first
										KeyValuePair <MotileActionPriority, MotileAction> nextNewAction = mNewActions.Dequeue();
										MotileActionPriority priority = nextNewAction.Key;
										MotileAction action = nextNewAction.Value;
										if (action != null) {
												//check the priority of the top action
												if (State.Actions.Count > 0) {
														//if we have actions that AREN'T the base action
														switch (priority) {	//check the priority of the new action against the
														//top action's yield setting
																case MotileActionPriority.ForceBase:
																		//top action doesn't matter, push it to the base
																		//we don't have to finish the current base action
																		//because it's not active
																		State.BaseAction.CopyFrom(action);
																		//we also don't bother to start it because it's at the base
																		break;

																case MotileActionPriority.ForceTop:
																		switch (topAction.YieldBehavior) {
																				case MotileYieldBehavior.DoNotYield:
																						//the top action won't let go
																						//see if the interrupt behavior will let this action be stored
																						switch (action.YieldBehavior) {
																								case MotileYieldBehavior.YieldAndFinish:
																								case MotileYieldBehavior.DoNotYield:
																										//well crap, looks like motile action failed
																										//don't add it anywhere
																										action.State = MotileActionState.Error;
																										action.Error = MotileActionError.PriorityConflict;
																										break;

																								case MotileYieldBehavior.YieldAndWait:
																								default:
																										//put it in the normal place, above the base action
																										//interrupt the action - if it's not supposed to reset it may expire
																										yield return InterruptAction(action);
																										State.Actions.Add(action);
																										break;
																						}
																						break;

																				case MotileYieldBehavior.YieldAndFinish:
																						//if the top action will finish,
																						//wait while we finish the top action
																						yield return StartCoroutine(FinishAction(topAction));
																						//then add the next action
																						State.Actions.Insert(0, action);
																						break;

																				case MotileYieldBehavior.YieldAndWait:
																				default:
																						//if the top action will wait, interrupt it
																						yield return StartCoroutine(InterruptAction(topAction));
																						State.Actions.Insert(0, action);
																						break;
																		}
																		break;

																case MotileActionPriority.Next:
																		//insert it *before* the top
																		if (State.Actions.Count > 0) {	//if we actually have a top action (0), insert it after the action
																				State.Actions.Insert(1, action);
																		} else {	//otherwise just add it normally
																				State.Actions.Add(action);
																		}
																		break;


																case MotileActionPriority.Normal:
																default:
																		//insert at the back
																		State.Actions.Add(action);
																		break;
														}
												} else {	//otherwise
														switch (priority) {
																case MotileActionPriority.ForceBase:
																		//we have to finish the base action
																		State.BaseAction.CopyFrom(action);
																		break;

																case MotileActionPriority.ForceTop:
																case MotileActionPriority.Normal:
																		//add the action to the regular queue
																		State.Actions.Add(action);
																		break;
														}
												}
										}
								}
								//once we're done dealing with new actions
								//remove any actions that are finished or expired
								List <MotileAction> actions = new List<MotileAction>();
								actions.AddRange(State.Actions);
								for (int i = actions.Count - 1; i >= 0; i--) {
										MotileAction checkAction = actions[i];
										if (checkAction == null
										    || checkAction.State == MotileActionState.Finished
										    || checkAction.State == MotileActionState.Error) {	//get rid of it
												actions.RemoveAt(i);
										}
								}
								State.Actions = actions;

								//now that we're sure our top action is legitimate
								//get the new top action
								topAction = TopAction;
								//check to see if the action has been asked to finish externally
								if (topAction.FinishCalledExternally) {	//if finish was called externally deal with that now
										//this should only get called once, since FinishAction sets
										//to state 'Finishing' immediately
										yield return StartCoroutine(FinishAction(topAction));
								} else {//otherwise deal with the action by state
										switch (topAction.State) {
												case MotileActionState.NotStarted:
												case MotileActionState.Waiting:
														//removing a finished top action may have opened up a waiting action
														//wait for the top action to start before moving on
														yield return StartCoroutine(StartAction(topAction));
														break;

												case MotileActionState.Started:
														//if the action has started, update it
														yield return StartCoroutine(UpdateAction(topAction));
														//then check to see if it has expired
														if (!topAction.BaseAction) {
																yield return StartCoroutine(UpdateExpiration(topAction));
														}
														break;

												default:
														//if we're finished/finishing, or still starting
														//don't do anything, this will be resolved in the next loop
														break;
										}
								}
								//wait a tick
								while (WorldClock.SkippingAhead) {
										yield return null;
								}
								yield return null;
						}
						//we're dead, blearg
						yield break;
				}

				protected IEnumerator StartAction(MotileAction action)
				{		//TODO decide if a coroutine is really necessary here
						//open up the door for mod-supplied delegtates
						//do some general cleanup - if we're starting an action, we want to start from scratch
						if (GoalObject == null) {
								action.Cancel();
								yield break;
						}

						while (!IsGrounded) {
								//TODO might be a good idea to just break here
								yield return null;
						}

						GoalObject.parent = worlditem.Group.transform;
						GoalObject.position = mTr.position;// + transform.forward;
						rvoController.PositionLocked = false;
						rvoController.RotationLocked = false;
						TargetMovementSpeed = 0.0f;
						CurrentRotationChangeSpeed = State.MotileProps.RotationChangeSpeed;
						//get the default pathfinding method
						action.Method = GetDefaultGoToMethod(State.MotileProps.DefaultGoToMethod, action.Method);

						//okay, now handle the new action
						if (action.State != MotileActionState.Waiting || !action.ResetAfterInterrupt) {	//if we're NOT resuming OR we're supposed to reset on resuming
								action.WTStarted = WorldClock.Time;
						}
						action.State = MotileActionState.Starting;
						bool started = false;
						switch (action.Type) {
								case MotileActionType.FocusOnTarget:
										//start any animations we're supposed to use
										started = true;
										yield return null;
										break;

								case MotileActionType.FollowRoutine:
										started = true;
										break;

								case MotileActionType.FollowGoal:
										GoalObject.position = action.LiveTarget.Position;
										started = true;
										break;

								case MotileActionType.FleeGoal:
										if (action.HasLiveTarget) {
												GoalObject.position = action.LiveTarget.Position;
										}
										started = true;
										break;

								case MotileActionType.WanderIdly:
										GoalObject.position = transform.position;
										started = true;
										break;

								case MotileActionType.FollowTargetHolder:
										if (!action.HasLiveTarget) {
												//TODO get live target?
										}
										if (action.LiveTargetHolder == null) {
												GoalHolder = action.LiveTarget.gameObject.GetOrAdd <RVOTargetHolder>();
										} else {
												GoalHolder = action.LiveTargetHolder;
										}
										started = true;
										break;

								case MotileActionType.GoToActionNode:
										//wait a tick to let the live target load
										yield return null;
										if (LastOccupiedNode != null) {	//////Debug.Log ("Vacating last action node");
												LastOccupiedNode.VacateNode(worlditem);
										}

										if (!action.HasLiveTarget) {
												//get live target
												ActionNodeState nodeState = null;
												if (GameWorld.Get.GetActionNodeState(ref nodeState, action.Target, false, false, null)) {	//if we could find the node
														yield return null;
														//wait a tick to let it load
														if (nodeState.IsLoaded) {	//if the action node is loaded, great! set live target
																if (nodeState.actionNode.TryToReserve(worlditem)) {
																		action.LiveTarget = nodeState.actionNode;
																} else {
																		action.State = MotileActionState.Error;
																		action.Error = MotileActionError.TargetInaccessible;
																}
														} else {//otherwise we have a problem...
																//the node isn't loaded
																action.State = MotileActionState.Error;
																action.Error = MotileActionError.TargetNotLoaded;
														}
												} else {//this node can't be found
														action.State = MotileActionState.Error;
														action.Error = MotileActionError.TargetNotFound;
												}
										}
										started = true;
										break;

								case MotileActionType.Wait:
								default:
										//waiting is simple, just wait
										TargetMovementSpeed = 0.0f;
										started = true;
										break;
						}

						if (started) {
								if (action.State != MotileActionState.Error) {	//preserve the error
										action.State = MotileActionState.Started;
								}
								//send messages
								action.OnStartAction.SafeInvoke();
								action.OnStartAction = null;
						}
						//wait for startup to end (not implemented)
						yield return null;
						yield break;
				}

				protected IEnumerator FinishAction(MotileAction action)
				{
						if (action.BaseAction) {
								yield break;
						}

						action.State = MotileActionState.Finishing;
						bool finished = false;
						switch (action.Type) {
								case MotileActionType.FocusOnTarget:
										finished = true;
										break;

								case MotileActionType.FollowGoal:
										finished = true;
										break;

								case MotileActionType.FollowRoutine:
										finished = true;
										break;

								case MotileActionType.WanderIdly:
										finished = true;
										break;

								case MotileActionType.FollowTargetHolder:
										GoalHolder = null;
										//move goal to group transform
										//this will stop the target holder from using it
										finished = true;
										break;

								case MotileActionType.GoToActionNode:
										//if we're at the action node, vacate the node
										//if we're not at the action node, do nothing
										if (LastOccupiedNode == null && action.HasLiveTarget && action.LiveTarget.IOIType == ItemOfInterestType.ActionNode) {	//if we actually have a live target
												ActionNode node = action.LiveTarget.node;
												if (!node.IsOccupant(worlditem)) {	//try to occupy it one last time
														node.TryToOccupyNode(worlditem);
														//if we don't make it oh well
												}
										}
										finished = true;
										break;

								case MotileActionType.Wait:
								default:
										finished = true;
										break;
						}

						if (finished) {
								if (action.State != MotileActionState.Error) {	//preserve the error
										action.State = MotileActionState.Finished;
								}
								action.WTFinished = WorldClock.Time;
								mLastFinishedAction = action;
								//send final messages and whatnot
								action.OnFinishAction.SafeInvoke();
								action.OnFinishAction = null;
								GoalObject.parent = worlditem.Group.transform;
						}
						//wait for finish to end (not implemented)
						yield return null;
						//force refresh hud
						rvoController.PositionLocked = false;
						rvoController.RotationLocked = false;
						worlditem.RefreshHud();
						//action state is finsihed
						yield break;
				}

				protected IEnumerator InterruptAction(MotileAction action)
				{
						action.State = MotileActionState.Waiting;
						action.OnInterruptAction.SafeInvoke();
						action.OnInterruptAction = null;
						//wait for interrupt to end (not implemented)
						yield return null;
						yield break;
				}

				protected IEnumerator UpdateAction(MotileAction action)
				{		//TODO find a way to let the action supply the update function
						//so mods can supply their own update functions
						//this is dumb
						switch (action.Type) {
								case MotileActionType.FocusOnTarget:
										yield return StartCoroutine(UpdateFocusOnTarget(action));
										break;

								case MotileActionType.FollowGoal:
										yield return StartCoroutine(UpdateFollowGoal(action));
										break;

								case MotileActionType.FollowRoutine:
										yield return StartCoroutine(UpdateFollowRoutine(action));
										break;

								case MotileActionType.FollowTargetHolder:
										yield return StartCoroutine(UpdateFollowTargetHolder(action));
										break;

								case MotileActionType.GoToActionNode:
										yield return StartCoroutine(UpdateGoToActionNode(action));
										break;

								case MotileActionType.WanderIdly:
										yield return StartCoroutine(UpdateWanderIdly(action));
										break;

								case MotileActionType.FleeGoal:
										yield return StartCoroutine(UpdateFleeGoal(action));
										break;

								case MotileActionType.Wait:
								default:
										yield return StartCoroutine(UpdateWait(action));
										break;
						}
						yield return null;
						yield break;
				}

				protected IEnumerator UpdateExpiration(MotileAction action)
				{		//TODO this doesn't have to be a coroutine
						bool expire = false;
						switch (action.Expiration) {
								case MotileExpiration.Duration:
										double expireTime = action.WTStarted + WorldClock.RTSecondsToGameSeconds(action.RTDuration);
										expire = WorldClock.Time > expireTime;
										break;

								case MotileExpiration.TargetInRange:
										if (action.HasLiveTarget) {	//are we close enough to our target?
												expire = (Vector3.Distance(action.LiveTarget.Position, transform.position) < action.Range);
										}
										//if no live target get non-live target and measure (?)
										break;

								case MotileExpiration.TargetOutOfRange:
										if (action.HasLiveTarget) {	//are we too far from our target?
												expire = (Vector3.Distance(action.LiveTarget.Position, transform.position) > action.OutOfRange);
										}
										//if no live target get non-live target and measure (?)
										break;

								case MotileExpiration.Never:
								default:
										break;
						}

						if (expire) {
								yield return StartCoroutine(FinishAction(action));
						}
						yield break;
				}

				protected IEnumerator UpdateFollowTargetHolder(MotileAction action)
				{
						float minimumSpeed = 0f;
						float minimumRotationChangeSpeed = 0f;
						if (GoalHolder == null) {//if the target holder is gone or the target holder is no longer managing our goal
								//we're finished if we don't have a target
								yield return StartCoroutine(FinishAction(action));
						} else {
								//otherwise proceed normally
								//first make sure the target holder is actually managing us
								if (GoalObject.parent != GoalHolder.transform) {
										//oh snape we've lost the connection, try to get it back
										switch (action.FollowType) {
												case MotileFollowType.Follower:
														GoalHolder.AddGroundFollower(GoalObject);
														break;

												case MotileFollowType.Stalker:
														minimumSpeed = State.MotileProps.SpeedIdleWalk;
														GoalHolder.AddGroundStalker(GoalObject);
														break;

												case MotileFollowType.Attacker:
														//attacking is a much more aggressive form of stalking
														//use our top speed to get where we need to go
														minimumSpeed = State.MotileProps.SpeedAttack;
														minimumRotationChangeSpeed = State.MotileProps.RotationChangeSpeed * 2f;
														GoalHolder.AttackOrStalk(GoalObject, true, ref action.FollowDirection);
														break;

												case MotileFollowType.Companion:
												default:
														GoalHolder.AddCompanion(GoalObject);
														break;
										}
								}
								//are we close enough to stop?
								//TargetRotation = rvoController.targetRotation;
								float distanceFromHolder = Vector3.Distance(transform.position, GoalObject.position);
								float distanceFromTarget = Vector3.Distance(transform.position, GoalHolder.transform.position);
								//use the range variable for our distance check
								if (distanceFromTarget <= action.Range) {	//don't spaz out - stop moving and face the target
										//if this results in a 'TargetInRange' expiration it'll be handled below
										action.IsInRange = true;
										if (action.FollowType == MotileFollowType.Companion) {
												//companions wait within the range for instructions
												TargetMovementSpeed = 0f;
										} else {
												//other follow types keep trying to make it to their target
												TargetMovementSpeed = State.MotileProps.SpeedIdleWalk;
										}
								} else if (distanceFromTarget <= action.Range * 1.5) {
										action.IsInRange = false;
										rvoController.PositionLocked = false;
										rvoController.RotationLocked = false;
										//don't stop, but do slow down a bit
										TargetMovementSpeed = State.MotileProps.SpeedWalk;
								} else {	//run and catch up!
										action.IsInRange = false;
										rvoController.PositionLocked = false;
										rvoController.RotationLocked = false;
										TargetMovementSpeed = State.MotileProps.SpeedRun;
								}
								//we don't need to update this one very often
								TargetMovementSpeed = Mathf.Max((float)TargetMovementSpeed, minimumSpeed);
								CurrentRotationChangeSpeed = Mathf.Max(State.MotileProps.RotationChangeSpeed, minimumRotationChangeSpeed);
								yield return null;
						}
				}

				protected IEnumerator UpdateFollowGoal(MotileAction action)
				{
						if (action.HasLiveTarget) {
								GoalObject.position = action.LiveTarget.Position;
								if (action.IsInRange) {
										rvoController.PositionLocked = true;
										rvoController.RotationLocked = false;
										TargetMovementSpeed = State.MotileProps.SpeedIdleWalk;
										//TargetRotation = rvoController.targetRotation;
								} else {
										float distanceFromTarget = Vector3.Distance(transform.position, GoalObject.position);
										if (distanceFromTarget <= action.Range * 2.0f) {
												rvoController.PositionLocked = false;
												rvoController.RotationLocked = false;
												TargetMovementSpeed = State.MotileProps.SpeedWalk;
												//TargetRotation = rvoController.targetRotation;
												//if (rvoController.velocity != Vector3.zero) {
												//TargetRotation = Quaternion.LookRotation (rvoController.velocity);
												//}
												//mAgent.repathRate = 1.5f;
												//rvoController.SetTarget (GoalObject.position);
										} else {
												rvoController.PositionLocked = false;
												rvoController.RotationLocked = false;
												TargetMovementSpeed = State.MotileProps.SpeedRun;
												//TargetRotation = rvoController.targetRotation;
												//if (rvoController.velocity != Vector3.zero) {
												//TargetRotation = Quaternion.LookRotation (rvoController.velocity);
												//}
												//mAgent.repathRate = 1.5f;
												//rvoController.SetTarget (GoalObject.position);
										}
								}
						} else {
								action.Cancel();
						}
						yield break;
				}

				protected IEnumerator UpdateFollowRoutine(MotileAction action)
				{
						yield return null;
						yield break;
						//TODO: Come back and fix this.
				}

				protected IEnumerator UpdateGoToActionNode(MotileAction action)
				{
						if (action.HasLiveTarget) {
								//rvoController.usePath = (action.Method == MotileGoToMethod.Pathfinding);
								GoalObject.position = action.LiveTarget.Position;
								ActionNode node = null;
								if (action.LiveTarget.IOIType == ItemOfInterestType.ActionNode) {	//see if we're there yet
										node = action.LiveTarget.node;
										float distanceFromTarget = Vector3.Distance(transform.position, GoalObject.position);
										//use the range variable for our distance check
										if (distanceFromTarget <= action.Range) {
												//don't spaz out, slow down and try to occupy the node
												rvoController.PositionLocked = false;
												rvoController.RotationLocked = false;
												TargetMovementSpeed = State.MotileProps.SpeedWalk;
												//if (rvoController.velocity != Vector3.zero) {
												//TargetRotation = Quaternion.LookRotation (rvoController.velocity);
												//}
												//rvoController.SetTarget (GoalObject.position);
												//can we occupy this thing?
												if (node.CanOccupy(worlditem)) {	//hooray! we can occupy it
														if (node.TryToOccupyNode(worlditem)) {	//we've occupied it, huzzah
																LastOccupiedNode = node;
																TargetMovementSpeed = 0.0f;
																yield return StartCoroutine(FinishAction(action));
														}
														//if we didn't occupy it, it might mean we're not close enough
														//because our range may be larger than the node range
														//so try again next frame
												} else {	//whoops, node is inaccessible
														//set to error
														action.State = MotileActionState.Error;
														action.Error = MotileActionError.TargetInaccessible;
												}
										} else if (distanceFromTarget <= action.Range * 1.5) {
												//don't stop, but do slow down a bit
												rvoController.PositionLocked = false;
												rvoController.RotationLocked = false;
												TargetMovementSpeed = State.MotileProps.SpeedWalk;
												//if (rvoController.velocity != Vector3.zero) {
												//TargetRotation = Quaternion.LookRotation (rvoController.velocity);
												//}
												//mAgent.repathRate = 2.5f;
												//rvoController.SetTarget (GoalObject.position);
										} else {
												//run and catch up!
												rvoController.PositionLocked = false;
												rvoController.RotationLocked = false;
												TargetMovementSpeed = State.MotileProps.SpeedRun;
												//if (rvoController.velocity != Vector3.zero) {
												//TargetRotation = Quaternion.LookRotation (rvoController.velocity);
												//}
												//mAgent.repathRate = 1.5f;
												//rvoController.SetTarget (GoalObject.position);
										}
								} else {	//weird, it got unloaded for some reason
										action.State = MotileActionState.Error;
										action.Error = MotileActionError.TargetNotLoaded;
								}
						} else {	//weird, live target is gone for some reason
								//try to get it again
								//(not implemented)
								action.State = MotileActionState.Error;
								action.Error = MotileActionError.TargetNotLoaded;
						}
						//otherwise get live target
						yield return null;
						yield break;
				}

				protected IEnumerator UpdateFocusOnTarget(MotileAction action)
				{
						if (action.HasLiveTarget) {	//move the focus object to the live target's position
								GoalObject.position = action.LiveTarget.Position;
						}
						//if we don't have a live target then it's probably being manipulated externally
						//Vector3 focusDirection = Vector3Extensions.Direction2D (GoalObject.position, transform.position);
						//TargetRotation = Quaternion.LookRotation (focusDirection);
						TargetMovementSpeed = 0.0f;
						rvoController.PositionLocked = true;
						rvoController.RotationLocked = false;
						//wait a bit
						yield return null;
						//we're done
						yield break;
				}

				protected void SendGoalToRandomPosition(Vector3 origin, float maxRange, float minRange)
				{
						mRandomDirection = UnityEngine.Random.insideUnitSphere.normalized.WithY(0f) * UnityEngine.Random.Range(minRange, maxRange);
						mRandomPosition = mRandomDirection + origin;
						mRandomTerrainHit = this.terrainHit;//copy terrain hit props (overhang etc)
						mRandomTerrainHit.feetPosition = mRandomPosition;
						if (worlditem.Group.Props.Interior) {
								//TODO get interior position
						} else {
								mRandomPosition.y = GameWorld.Get.TerrainHeightAtInGamePosition(ref mRandomTerrainHit);
						}
						GoalObject.position = mRandomPosition;
				}

				protected Vector3 mRandomPosition;
				protected Vector3 mRandomDirection;
				protected GameWorld.TerrainHeightSearch mRandomTerrainHit;

				protected IEnumerator UpdateFleeGoal(MotileAction action)
				{
						TargetMovementSpeed = State.MotileProps.SpeedRun;

						if (action.LiveTarget == null) {
								action.TryToFinish();
								yield break;
						}

						mAvoid = action.LiveTarget.Position;
						mFleeDirection = (mPosition - mAvoid).normalized;
						mGoalPosition = mPosition;

						if (action.TerritoryType == MotileTerritoryType.Den) {
								//chose a position that's within the den radius
								float distanceToDenEdge = Vector3.Distance(action.TerritoryBase.transform.position, mTr.position);
								mGoalPosition = mPosition + (mFleeDirection * Mathf.Min(distanceToDenEdge, action.Range));
						} else {
								//just move the goal
								mGoalPosition = mPosition + (mFleeDirection * action.Range);
						}
						mRandomTerrainHit = this.terrainHit;
						mRandomTerrainHit.feetPosition = mGoalPosition;
						mGoalPosition.y = GameWorld.Get.TerrainHeightAtInGamePosition(ref mRandomTerrainHit);
						GoalObject.position = mGoalPosition;
						yield return new WaitForSeconds(0.1f);

						yield break;
				}

				protected Vector3 mAvoid;
				protected Vector3 mFleeDirection;
				protected Vector3 mGoalPosition;

				protected IEnumerator UpdateWanderIdly(MotileAction action)
				{
						rvoController.PositionLocked = false;
						rvoController.RotationLocked = false;
						TargetMovementSpeed = State.MotileProps.SpeedIdleWalk;
						if (HasReachedGoal(rvoController.radius)) {
								if (UnityEngine.Random.value < (State.MotileProps.IdleWanderThreshold / 100)) {
										//choose a new direction no matter what
										switch (action.TerritoryType) {
												case MotileTerritoryType.Den:
														SendGoalToRandomPosition(action.TerritoryBase.transform.position, action.TerritoryBase.Radius, action.TerritoryBase.InnerRadius);
														break;

												case MotileTerritoryType.None:
												default:
														SendGoalToRandomPosition(GoalObject.position, action.Range, action.Range / 2f);
														break;
										}
								}
						} else if (UnityEngine.Random.value < (State.MotileProps.IdleWaitThreshold / 100)) {
								SendGoalToRandomPosition(transform.position, action.Range, 0.5f);//this will make us look around
						}
						yield return new WaitForSeconds(0.25f);
						yield break;
				}

				public void OnDrawGizmos()
				{
						if (!mInitialized)
								return;

						if (GoalObject != null) {
								Gizmos.color = Color.green;
								Gizmos.DrawLine(mPosition, GoalObject.position);
						}

						if (terrainHit.isGrounded) {
								Gizmos.color = Color.cyan;
						} else {
								Gizmos.color = Color.red;
						}
						Gizmos.DrawLine(terrainHit.feetPosition, terrainHit.feetPosition + Vector3.up * terrainHit.overhangHeight);
				}

				protected IEnumerator UpdateWait(MotileAction action)
				{
						TargetMovementSpeed = 0.0f;
						if (action.HasLiveTarget) {
								GoalObject.position = action.LiveTarget.Position;
						} else {
								//look at stuff randomly
								if (UnityEngine.Random.value < (State.MotileProps.IdleWanderThreshold / 100)) {
										//Debug.Log ("Waiting in motile, sending goal to random position");
										SendGoalToRandomPosition(transform.position, 0.125f, 0.125f);
								}
						}
						yield return new WaitForSeconds(0.125f);
						yield break;
				}

				#endregion

				public static MotileGoToMethod GetDefaultGoToMethod(MotileGoToMethod Default, MotileGoToMethod Failsafe)
				{
						if (Default == MotileGoToMethod.UseDefault) {
								return Failsafe;
						}
						return Default;
				}
				//a list of action/priority pairs that are handled in the order they are received
				protected MotileInstructions mMotileInstructions;
				protected Queue <KeyValuePair <MotileActionPriority, MotileAction>>	mNewActions = new Queue <KeyValuePair <MotileActionPriority, MotileAction>>();
				protected bool mHandlingNewActions = false;
				protected MotileAction mLastFinishedAction = null;
				protected double mCurrentMovementSpeed;
				protected double mCurrentRotationSpeed;
				protected bool mDoingActionsOverTime = false;
				protected bool mRagdoll = false;
				protected Transform mTr;
				GameWorld.TerrainHeightSearch terrainHit = new GameWorld.TerrainHeightSearch();
				protected double mLastGroundedHeight = 0f;
				protected double mLastGroundedTime;
				protected Vector3 mPosition;
				protected Vector3 mDesiredDirection;
				protected Vector3 mDesiredLookDirection;
				protected Quaternion mVisibleRotation;
		}

		public interface IBodyOwner
		{
				Vector3 Position { get; set; }

				Quaternion Rotation { get; }

				WorldBody Body { get; set; }

				bool Initialized { get; }

				bool IsImmobilized { get; }

				bool IsGrounded { get; }

				bool IsRagdoll { get; }

				bool IsDestroyed { get; }

				double CurrentMovementSpeed { get; set; }

				double CurrentRotationSpeed { get; set; }

				int CurrentIdleAnimation { get; set; }
		}

		[Serializable]
		public class MotileState
		{
				public MotileProperties MotileProps = new MotileProperties();
				//how fast we run, walk, etc.
				public List <MotileAction> Actions = new List <MotileAction>();
				//what we're supposed to be doing now
				public MotileAction BaseAction = new MotileAction();
				//what we do when there's nothing else to do (usually routine)
				public double MovementFatigue = 0.0f;
				//how much the character gets fatigued by running / walking, usually 0
				public List <string> QuestPointsReached = new List <string>();
				protected MotileAction mBaseAction;
		}

		[Serializable]
		public class MotileAction
		{
				public static MotileAction GoTo(MobileReference target)
				{
						MotileAction newAction = new MotileAction();
						newAction.Type = MotileActionType.GoToActionNode;
						newAction.Target = target;
						newAction.Expiration = MotileExpiration.Never;
						newAction.YieldBehavior = MotileYieldBehavior.YieldAndFinish;
						return newAction;
				}

				public static MotileAction GoTo(ActionNodeState state)
				{
						MotileAction newAction = new MotileAction();
						newAction.Type = MotileActionType.GoToActionNode;
						if (state.IsLoaded) {
								newAction.LiveTarget = state.actionNode;
						}
						newAction.Target = new MobileReference(state.Name, state.ParentGroupPath);
						newAction.Expiration = MotileExpiration.Never;
						newAction.YieldBehavior = MotileYieldBehavior.YieldAndFinish;
						newAction.IdleAnimation = state.IdleAnimation;
						return newAction;
				}

				public static MotileAction Wait(ActionNodeState state)
				{
						MotileAction newAction = new MotileAction();
						newAction.Type = MotileActionType.Wait;
						if (state.IsLoaded) {
								newAction.LiveTarget = state.actionNode;
						}
						newAction.Target = new MobileReference(state.Name, state.ParentGroupPath);
						newAction.Expiration = MotileExpiration.Never;
						newAction.YieldBehavior = MotileYieldBehavior.YieldAndFinish;
						newAction.IdleAnimation = state.IdleAnimation;
						return newAction;
				}

				public static MotileAction Wait(int IdleAnimation)
				{
						MotileAction newAction = new MotileAction();
						newAction.Type = MotileActionType.Wait;
						newAction.Target = MobileReference.Empty;
						newAction.Expiration = MotileExpiration.Never;
						newAction.YieldBehavior = MotileYieldBehavior.YieldAndFinish;
						newAction.IdleAnimation	= IdleAnimation;
						return newAction;
				}

				public static MotileAction FocusOnPlayerInRange(float range)
				{
						MotileAction newAction = new MotileAction();
						newAction.Type = MotileActionType.FocusOnTarget;
						newAction.Target.FileName = "[Player]";
						newAction.LiveTarget = Player.Local;
						newAction.Expiration = MotileExpiration.TargetOutOfRange;
						newAction.OutOfRange = range;
						newAction.YieldBehavior = MotileYieldBehavior.YieldAndFinish;
						return newAction;
				}

				public static MotileAction TalkToPlayer {
						get {
								MotileAction newAction = new MotileAction();
								newAction.Type = MotileActionType.FocusOnTarget;
								newAction.Target.FileName = "[Player]";
								newAction.LiveTarget = Player.Local;
								newAction.Expiration = MotileExpiration.Never;
								newAction.YieldBehavior = MotileYieldBehavior.DoNotYield;
								newAction.IdleAnimation = GameWorld.Get.FlagByName("CharacterIdleAnimation", "Conversation");
								return newAction;
						}
				}

				public IEnumerator WaitForActionToStart(float interval)
				{
						bool wait = !BaseAction;
						while (wait) {
								wait = (State != MotileActionState.Finished && State != MotileActionState.Error && State != MotileActionState.Started);
								yield return new WaitForSeconds(interval);
						}
						yield break;
				}

				public IEnumerator WaitForActionToFinish(float interval)
				{
						bool wait = !BaseAction;
						while (wait) {
								wait = (State != MotileActionState.Finished && State != MotileActionState.Error);
								yield return new WaitForSeconds(interval);
						}
						yield break;
				}

				public void Reset()
				{
						mFinishCalledExternally = false;
						WTAdded = -1.0f;
						WTStarted = -1.0f;
						WTFinished = -1.0f;
						Error = MotileActionError.None;
						State = MotileActionState.NotStarted;
						OnFinishAction = null;
				}

				public void TryToFinish()
				{
						if (!BaseAction) {
								mFinishCalledExternally = true;
						}
				}

				public void Cancel()
				{
						if (!BaseAction) {
								State = MotileActionState.Error;
								Error = MotileActionError.Canceled;
						}
				}

				public bool HasLiveTarget {
						get {
								return LiveTarget != null;
						}
				}

				public bool FinishCalledExternally {
						get {
								if (!BaseAction && State == MotileActionState.Finished || State == MotileActionState.Finishing) {	//if the state is finished then it doesn't matter any more
										return false;
								}
								return mFinishCalledExternally;
						}
				}

				public bool IsFinished {
						get {
								return !BaseAction && State == MotileActionState.Error
								|| State == MotileActionState.Finished;
						}
				}

				public bool HasStarted {
						get { return State == MotileActionState.Started; }
				}

				public bool IsInRange {
						get;
						set;
				}

				public void CopyFrom(MotileAction action)
				{
						Type = action.Type;
						Target = action.Target;
						FollowType = action.FollowType;
						FollowDirection = action.FollowDirection;
						Method = action.Method;
						Expiration = action.Expiration;
						YieldBehavior = action.YieldBehavior;
						Instructions = action.Instructions;
						RTDuration = action.RTDuration;
						Range = action.Range;
						OutOfRange = action.OutOfRange;
						PathName = action.PathName;
						IdleAnimation = action.IdleAnimation;
						LiveTarget = action.LiveTarget;

						LiveTarget = action.LiveTarget;
						TerritoryBase = action.TerritoryBase;
						OnFinishAction = action.OnFinishAction;
				}

				public string Name = "[Noname]";
				public MotileActionState State = MotileActionState.NotStarted;
				public MotileActionType Type = MotileActionType.GoToActionNode;
				public MobileReference Target = new MobileReference();
				public MotileFollowType FollowType = MotileFollowType.Follower;
				public MapDirection FollowDirection = MapDirection.I_None;
				public MotileGoToMethod Method = MotileGoToMethod.Pathfinding;
				public MotileExpiration Expiration = MotileExpiration.Duration;
				public MotileYieldBehavior YieldBehavior = MotileYieldBehavior.YieldAndWait;
				public MotileActionError Error = MotileActionError.None;

				public MotileTerritoryType TerritoryType {
						get {
								if (TerritoryBase != null) {
										return MotileTerritoryType.Den;
								}
								return MotileTerritoryType.None;
						}
				}

				[BitMask(typeof(MotileInstructions))]
				public MotileInstructions Instructions = MotileInstructions.InheritFromBase;
				public bool ResetAfterInterrupt = true;
				public bool BaseAction = false;
				public double WTAdded = -1.0f;
				public double WTStarted = -1.0f;
				public double WTFinished = -1.0f;
				public double RTDuration = 0.0f;
				public float Range = 1.0f;
				public float OutOfRange = 10.0f;
				public string PathName = null;
				[FrontiersBitMask("IdleAnimation")]
				public int IdleAnimation = 1;
				public Icon HudIcon = Icon.Empty;
				[XmlIgnore]
				[NonSerialized]
				public IItemOfInterest LiveTarget = null;
				[XmlIgnore]
				[NonSerialized]
				public RVOTargetHolder LiveTargetHolder = null;

				[XmlIgnore]
				//[NonSerialized]
				public ITerritoryBase TerritoryBase {
						get {
								return mTerritoryBase;
						}
						set {
								mTerritoryBase = value;
						}
				}

				[NonSerialized]
				protected ITerritoryBase mTerritoryBase = null;
				protected bool mFinishCalledExternally = false;
				[XmlIgnore]
				[NonSerialized]
				public Action OnStartAction;
				[XmlIgnore]
				[NonSerialized]
				public Action OnInterruptAction;
				[XmlIgnore]
				[NonSerialized]
				public Action OnFinishAction;
		}

		public enum MotileTerritoryType
		{
				None,
				Den,
		}
}