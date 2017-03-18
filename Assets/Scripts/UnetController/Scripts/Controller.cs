﻿//#define SIMULATE
#define CLIENT_TRUST

using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine.Networking;
using UnityEngine.Events;

namespace GreenByteSoftware.UNetController {

	#region classes

	//Input management
	public interface IPLayerInputs {
		float GetMouseX ();
		float GetMouseY ();
		float GetMoveX ();
		float GetMoveY ();
		bool GetJump ();
		bool GetCrouch ();
		bool GetSprint ();
	}

	//Be sure to edit the binary serializable class in the extensions script accordingly
	[System.Serializable]
	public struct Inputs
	{

		public Vector2 inputs;
		public float x;
		public float y;
		public bool jump;
		public bool crouch;
		public bool sprint;
		public int timestamp;

	}

	//Be sure to edit the binary serializable class in the extensions script accordingly
	[System.Serializable]
	public struct Results
	{
		public Vector3 position;
		public Quaternion rotation;
		public Vector3 groundNormal;
		public float camX;
		public Vector3 speed;
		public bool isGrounded;
		public bool jumped;
		public bool crouch;
		public float groundPoint;
		public float groundPointTime;
		public int timestamp;

		public Results (Vector3 pos, Quaternion rot, Vector3 gndNormal, float cam, Vector3 spe, bool ground, bool jump, bool crch, float gp, float gpt, int tick) {
			position = pos;
			rotation = rot;
			groundNormal = gndNormal;
			camX = cam;
			speed = spe;
			isGrounded = ground;
			jumped = jump;
			crouch = crch;
			groundPoint = gp;
			groundPointTime = gpt;
			timestamp = tick;
		}

		public override string ToString () {
			return "" + position + "\n"
			+ rotation + "\n"
			+ camX + "\n"
			+ speed + "\n"
			+ isGrounded + "\n"
			+ jumped + "\n"
			+ crouch + "\n"
			+ groundPoint + "\n"
			+ groundPointTime + "\n"
			+ timestamp + "\n";
		}
	}

	[System.Serializable]
	public class InputResult : MessageBase {
		public Inputs inp;
		public Results res;
	}

	[System.Serializable]
	public class InputSend : MessageBase {
		public Inputs inp;
	}

	public delegate void TickUpdateDelegate(Results res);
	public delegate void TickUpdateAllDelegate(Inputs inp, Results res);

	#endregion

	[NetworkSettings (channel=1)]
	public class Controller : NetworkBehaviour {

		public ControllerDataObject data;
		public ControllerInputDataObject dataInp;
		public MonoBehaviour inputsInterfaceClass;
		private IPLayerInputs _inputsInterface;

		public IPLayerInputs inputsInterface {
			get {
				if (_inputsInterface == null && inputsInterfaceClass != null)
					_inputsInterface = inputsInterfaceClass as IPLayerInputs;
				if (_inputsInterface == null)
					inputsInterfaceClass = null;
				return _inputsInterface;
			}
		}

		[System.NonSerialized]
		public CharacterController controller;

		private Transform _transform;
		public Transform myTransform {
			get {
				if (_transform == null)
					_transform = transform;
				return _transform;
			}
		}

		//Private variables used to optimize for mobile's instruction set
		private float strafeToSpeedCurveScaleMul;
		private float _strafeToSpeedCurveScale;

		private float snapInvert;

		Vector3 interpPos;

		private int _sendUpdates;

		public int sendUpdates {
			get { return _sendUpdates; }
		}

		private int currentFixedUpdates = 0;
		private int currentTFixedUpdates = 0;

		private int currentTick = 0;

		[System.NonSerialized]
		public List<Inputs> clientInputs;
		private Inputs curInput;
		private Inputs curInputServer;

		[System.NonSerialized]
		public List<Results> clientResults;
		private Results serverResults;
		private Results tempResults;
		private Results lastResults;

		private List<Results> sendResultsArray = new List<Results> (5);
		private Results sendResults;
		private Results sentResults;

		private List<Results> serverResultList;

		public Transform head;
		public Transform camTarget;
		public Transform camTargetFPS;

		private Vector3 posStart;
		private Vector3 posEnd;
		#pragma warning disable 0414 //Currently this variable is not in use anywhere, but we can not remove it so just suppress the warnings
		private float posEndG;
		#pragma warning restore 0414
		private Vector3 posEndO;
		private Quaternion rotStart;
		private Quaternion rotEnd;
		#pragma warning disable 0414
		private Quaternion rotEndO;
		private float headStartRot = 0f;
		private float headEndRot = 0f;

		private float groundPointTime;
		#pragma warning restore 0414
		private float startTime;

		private bool reconciliate = false;

		private int lastTick = -1;
		private bool receivedFirstTime;


    public TickUpdateDelegate tickUpdate;
		public TickUpdateAllDelegate tickUpdateDebug;

		#if (CLIENT_TRUST)
		private InputResult inpRes;
		#endif
		private InputSend inpSend;

		private NetworkWriter inputWriter;

		private NetworkClient _myClient;
		public NetworkClient myClient {
			get {
				if (_myClient == null && isLocalPlayer)
					_myClient = NetworkManager.singleton.client;
				return _myClient;
			}
		}
			
		const short inputMessage = 101;

		public override float GetNetworkSendInterval () {
			if (data != null)
				return data.sendRate;
			else
				return 0.1f;
		}

		private float _crouchSwitchMul = -1;

		public float crouchSwitchMul {
			get {
				if (_crouchSwitchMul >= 0)
					return _crouchSwitchMul;
				else if (data != null) {
					_crouchSwitchMul = 1 / (data.controllerCrouchSwitch / (Time.fixedDeltaTime * sendUpdates));
					return _crouchSwitchMul;
				}
				return 0;
			}
		}

		public override void OnStartLocalPlayer () {
			CameraControl.SetTarget (camTarget, camTargetFPS);
		}



		void OnStartServer () {
			SetupThings ();
		}

		void Awake () {
			SetupThings ();
		}

		public void SetupThings () {
			posStart = myTransform.position;
			rotStart = myTransform.rotation;

			posEnd = myTransform.position;
			rotEnd = myTransform.rotation;

			lastResults.position = posEnd;
			lastResults.rotation = rotEnd;
			serverResults.position = posEnd;
			serverResults.rotation = rotEnd;

		}

		[ServerCallback]
		public float GetCamY() {
			if (isLocalPlayer)
				return clientInputs [clientInputs.Count - 1].y;
			else
				return curInput.y;
		}

		void Start () {

			gameObject.name = Extensions.GenerateGUID ();

			if (data == null || dataInp == null) {
				Debug.LogError ("No controller data attached! Will not continue.");
				this.enabled = false;
				return;
			}

			if (data.snapSize > 0)
				snapInvert = 1f / data.snapSize;

			clientInputs = new List<Inputs>();
			clientResults = new List<Results>();
			serverResultList = new List<Results>();

			controller = GetComponent<CharacterController> ();
			curInput = new Inputs ();
			curInput.x = myTransform.rotation.eulerAngles.y;
			curInput.inputs = new Vector2 ();

			posStart = myTransform.position;
			rotStart = myTransform.rotation;

			posEnd = myTransform.position;
			rotEnd = myTransform.rotation;

			_sendUpdates = Mathf.Max(1, Mathf.RoundToInt (data.sendRate / Time.fixedDeltaTime));

			if (isServer) {
				curInput.timestamp = -1000;
				NetworkServer.RegisterHandler (inputMessage, OnSendInputs);
			}

			GameManager.RegisterController (this);
		}

		#if (CLIENT_TRUST)
		void SendInputs (Inputs inp, Results res) {
		#else
		void SendInputs (Inputs inp) {
		#endif

			if (!isLocalPlayer || isServer)
				return;

			if (inputWriter == null)
				inputWriter = new NetworkWriter ();
			if (inpSend == null)
				inpSend = new InputSend ();

			inputWriter.SeekZero();
			inputWriter.StartMessage(inputMessage);
			#if (CLIENT_TRUST)
			inpRes = new InputResult ();
			inpRes.inp = inp;
			inpRes.res = res;
			inputWriter.Write(inpRes);
			#else
			inpSend.inp = inp;
			inputWriter.Write(inpSend);
			#endif
			inputWriter.FinishMessage();

			myClient.SendWriter(inputWriter, GetNetworkChannel());
		}

		void OnSendInputs (NetworkMessage msg) {
			
			#if (CLIENT_TRUST)
			inpRes = msg.ReadMessage<InputResult> ();
			Inputs inp = inpRes.inp;
			Results res = inpRes.res;
			#else
			Inputs inp = msg.ReadMessage<InputSend> ().inp;
			#endif

			#if (SIMULATE)
			#if (CLIENT_TRUST)
			StartCoroutine (SendInputsC (inp, res));
			#else
			StartCoroutine (SendInputsC (inp));
			#endif
		}
		#if (CLIENT_TRUST)
		IEnumerator SendInputsC (Inputs inp, Results res) {
		#else
		IEnumerator SendInputsC (Inputs inp) {
		#endif
			yield return new WaitForSeconds (UnityEngine.Random.Range (0.21f, 0.28f));
			#endif

			if (!isLocalPlayer) {

				if (clientInputs.Count > data.clientInputsBuffer)
					clientInputs.RemoveAt (0);

				if (!ClientInputsContainTimestamp (inp.timestamp))
					clientInputs.Add (inp);

				#if (CLIENT_TRUST)
				tempResults = res;
				#endif

				currentTFixedUpdates += sendUpdates;

				if (data.debug && lastTick + 1 != inp.timestamp && lastTick != -1) {
					Debug.Log ("Missing tick " + lastTick + 1);
				}
				lastTick = inp.timestamp;
			}
		}

		//Results part
		public override void OnDeserialize (NetworkReader reader, bool initialState) {

			if (isServer)
				return;

			if (initialState) {
				sendResults.position = reader.ReadVector3 ();
				sendResults.rotation = reader.ReadQuaternion ();
				sendResults.groundNormal = reader.ReadVector3 ();
				sendResults.camX = reader.ReadSingle ();
				sendResults.speed = reader.ReadVector3 ();
				sendResults.isGrounded = reader.ReadBoolean ();
				sendResults.jumped = reader.ReadBoolean ();
				sendResults.crouch = reader.ReadBoolean ();
				sendResults.groundPoint = reader.ReadSingle ();
				sendResults.groundPointTime = reader.ReadSingle ();
				sendResults.timestamp = (int)reader.ReadPackedUInt32 ();
				OnSendResults (sendResults);
			} else {

				int count = (int)reader.ReadPackedUInt32 ();

				for (int i = 0; i < count; i++) {

					uint mask = reader.ReadPackedUInt32 ();

					if ((mask & (1 << 0)) != 0)
						sendResults.position = reader.ReadVector3 ();
					if ((mask & (1 << 1)) != 0)
						sendResults.rotation = reader.ReadQuaternion ();
					if ((mask & (1 << 2)) != 0)
						sendResults.groundNormal = reader.ReadVector3 ();
					if ((mask & (1 << 3)) != 0)
						sendResults.camX = reader.ReadSingle ();
					if ((mask & (1 << 4)) != 0)
						sendResults.speed = reader.ReadVector3 ();
					if ((mask & (1 << 5)) != 0)
						sendResults.isGrounded = reader.ReadBoolean ();
					if ((mask & (1 << 6)) != 0)
						sendResults.jumped = reader.ReadBoolean ();
					if ((mask & (1 << 7)) != 0)
						sendResults.crouch = reader.ReadBoolean ();
					if ((mask & (1 << 8)) != 0)
						sendResults.groundPoint = reader.ReadSingle ();
					if ((mask & (1 << 9)) != 0)
						sendResults.groundPointTime = reader.ReadSingle ();
					if ((mask & (1 << 10)) != 0)
						sendResults.timestamp = (int)reader.ReadPackedUInt32 ();
					OnSendResults (sendResults);
				}
			}
			
		}

		uint GetResultsBitMask (Results res1, Results res2) {
			uint mask = 0;
			if(res1.position != res2.position) mask |= 1 << 0;
			if(res1.rotation != res2.rotation) mask |= 1 << 1;
			if(res1.groundNormal != res2.groundNormal) mask |= 1 << 2;
			if(res1.camX != res2.camX) mask |= 1 << 3;
			if(res1.speed != res2.speed) mask |= 1 << 4;
			if(res1.isGrounded != res2.isGrounded) mask |= 1 << 5;
			if(res1.jumped != res2.jumped) mask |= 1 << 6;
			if(res1.crouch != res2.crouch) mask |= 1 << 7;
			if(res1.groundPoint != res2.groundPoint) mask |= 1 << 8;
			if(res1.groundPointTime != res2.groundPointTime) mask |= 1 << 9;
			if(res1.timestamp != res2.timestamp) mask |= 1 << 10;
			return mask;
		}

		public override bool OnSerialize (NetworkWriter writer, bool forceAll) {

			if (forceAll) {
				writer.Write(sendResults.position);
				writer.Write(sendResults.rotation);
				writer.Write(sendResults.groundNormal);
				writer.Write(sendResults.camX);
				writer.Write(sendResults.speed);
				writer.Write(sendResults.isGrounded);
				writer.Write(sendResults.jumped);
				writer.Write(sendResults.crouch);
				writer.Write(sendResults.groundPoint);
				writer.Write(sendResults.groundPointTime);
				writer.WritePackedUInt32((uint)sendResults.timestamp);

				sentResults = sendResults;
				return true;
			} else {

				writer.WritePackedUInt32 ((uint)sendResultsArray.Count);

				while (sendResultsArray.Count > 0) {

					sendResults = sendResultsArray [0];
					sendResultsArray.RemoveAt (0);

					uint mask = GetResultsBitMask (sendResults, sentResults);
					writer.WritePackedUInt32 (mask);

					if ((mask & (1 << 0)) != 0)
						writer.Write (sendResults.position);
					if ((mask & (1 << 1)) != 0)
						writer.Write (sendResults.rotation);
					if ((mask & (1 << 2)) != 0)
						writer.Write (sendResults.groundNormal);
					if ((mask & (1 << 3)) != 0)
						writer.Write (sendResults.camX);
					if ((mask & (1 << 4)) != 0)
						writer.Write (sendResults.speed);
					if ((mask & (1 << 5)) != 0)
						writer.Write (sendResults.isGrounded);
					if ((mask & (1 << 6)) != 0)
						writer.Write (sendResults.jumped);
					if ((mask & (1 << 7)) != 0)
						writer.Write (sendResults.crouch);
					if ((mask & (1 << 8)) != 0)
						writer.Write (sendResults.groundPoint);
					if ((mask & (1 << 9)) != 0)
						writer.Write (sendResults.groundPointTime);
					if ((mask & (1 << 10)) != 0)
						writer.WritePackedUInt32 ((uint)sendResults.timestamp);

					sentResults = sendResults;
				}
				return true;
			}
		}
			
		void OnSendResults (Results res) {

			if (isServer)
				return;

			#if (SIMULATE)
			StartCoroutine (SendResultsC (res));
		}

		IEnumerator SendResultsC (Results res) {
			yield return new WaitForSeconds (UnityEngine.Random.Range (0.21f, 0.38f));
			#endif

			if (isLocalPlayer) {

				foreach (Results t in clientResults) {
					if (t.timestamp == res.timestamp)
						Debug_UI.UpdateUI (posEnd, res.position, t.position, currentTick, res.timestamp);
				}

				if (serverResultList.Count > data.serverResultsBuffer)
					serverResultList.RemoveAt (0);

				if (!ServerResultsContainTimestamp (res.timestamp))
					serverResultList.Add (res);

				serverResults = SortServerResultsAndReturnFirst ();

				if (serverResultList.Count >= data.serverResultsBuffer)
					reconciliate = true;

			} else {
				currentTick++;

				if (!isServer) {
					serverResults = res;
					if (tickUpdate != null) tickUpdate(res);
				}

				if (currentTick > 2) {
					serverResults = res;
					posStart = posEnd;
					rotStart = rotEnd;
					headStartRot = headEndRot;
					headEndRot = res.camX;
					//if (Time.fixedTime - 2f > startTime)
						startTime = Time.fixedTime;
					//else
					//	startTime = Time.fixedTime - ((Time.fixedTime - startTime) / (Time.fixedDeltaTime * _sendUpdates) - 1) * (Time.fixedDeltaTime * _sendUpdates);
					posEnd = posEndO;
					rotEnd = rotEndO;
					groundPointTime = serverResults.groundPointTime;
					posEndG = serverResults.groundPoint;
					posEndO = serverResults.position;
					rotEndO = serverResults.rotation;
				} else {
					startTime = Time.fixedTime;
					serverResults = res;
					posStart = serverResults.position;
					rotStart = serverResults.rotation;
					headStartRot = headEndRot;
					headEndRot = res.camX;
					posEnd = posStart;
					rotEnd = rotStart;
					groundPointTime = serverResults.groundPointTime;
					posEndG = posEndO.y;
					posEndO = posStart;
					rotEndO = rotStart;

				}
			}
		}

		Results SortServerResultsAndReturnFirst () {

			Results tempRes;

			for (int x = 0; x < serverResultList.Count; x++) {
				for (int y = 0; y < serverResultList.Count - 1; y++) {
					if (serverResultList [y].timestamp > serverResultList [y + 1].timestamp) {
						tempRes = serverResultList [y + 1];
						serverResultList [y + 1] = serverResultList [y];
						serverResultList [y] = tempRes;
					}
				}
			}

			if (serverResultList.Count > data.serverResultsBuffer)
				serverResultList.RemoveAt (0);


			return serverResultList [0];
		}

		bool ServerResultsContainTimestamp (int timeStamp) {
			for (int i = 0; i < serverResultList.Count; i++) {
				if (serverResultList [i].timestamp == timeStamp)
					return true;
			}

			return false;
		}

		Inputs SortClientInputsAndReturnFirst () {

			Inputs tempInp;

			for (int x = 0; x < clientInputs.Count; x++) {
				for (int y = 0; y < clientInputs.Count - 1; y++) {
					if (clientInputs [y].timestamp > clientInputs [y + 1].timestamp) {
						tempInp = clientInputs [y + 1];
						clientInputs [y + 1] = clientInputs [y];
						clientInputs [y] = tempInp;
					}
				}
			}

			if (clientInputs.Count > data.clientInputsBuffer)
				clientInputs.RemoveAt (0);


			return clientInputs [0];
		}

		bool ClientInputsContainTimestamp (int timeStamp) {
			for (int i = 0; i < clientInputs.Count; i++) {
				if (clientInputs [i].timestamp == timeStamp)
					return true;
			}

			return false;
		}

		//Function which replays the old inputs if prediction errors occur
		void Reconciliate () {
			for (int i = 0; i < clientResults.Count; i++) {
				if (clientResults [i].timestamp == serverResults.timestamp) {
					clientResults.RemoveRange (0, i);
					for (int o = 0; o < clientInputs.Count; o++) {
						if (clientInputs [o].timestamp == serverResults.timestamp) {
							clientInputs.RemoveRange (0, o);
							break;
						}
					}
					break;
				}
			}

			Results oldRes = tempResults;

			tempResults = serverResults;

			controller.enabled = true;

			for (int i = 1; i < clientInputs.Count - 1; i++) {
				tempResults = MoveCharacter (tempResults, clientInputs [i], Time.fixedDeltaTime * sendUpdates, data.maxSpeedNormal);
			}

			if (Vector3.Distance (tempResults.position, oldRes.position) < 20f)
				tempResults = oldRes;

			groundPointTime = tempResults.groundPointTime;
			posEnd = tempResults.position;
			rotEnd = tempResults.rotation;
			posEndG = tempResults.groundPoint;

		}

		//Input gathering
		void Update () {
			if (isLocalPlayer) {
				if (inputsInterface == null)
					throw (new UnityException ("inputsInterface is not set!"));
				
				curInput.inputs.x = inputsInterface.GetMoveX ();
				curInput.inputs.y = inputsInterface.GetMoveY ();

				curInput.x = inputsInterface.GetMouseX ();
				curInput.y = inputsInterface.GetMouseY ();

				curInput.jump = inputsInterface.GetJump ();
				curInput.sprint = inputsInterface.GetSprint ();

				curInput.crouch = inputsInterface.GetCrouch ();
			
			}
		}

		//This is where the ticks happen
		void FixedUpdate () {

			if (data.strafeToSpeedCurveScale != _strafeToSpeedCurveScale) {
				_strafeToSpeedCurveScale = data.strafeToSpeedCurveScale;
				strafeToSpeedCurveScaleMul = 1f / data.strafeToSpeedCurveScale;
			}

			if (isLocalPlayer || isServer) {
				currentFixedUpdates++;
			}

			if (isLocalPlayer && currentFixedUpdates >= sendUpdates) {
				currentTick++;

				if (!isServer) {
					if (tickUpdate != null) tickUpdate (lastResults);
					clientResults.Add (lastResults);
				}

				if (clientInputs.Count >= data.inputsToStore)
					clientInputs.RemoveAt (0);

				clientInputs.Add (curInput);
				curInput.timestamp = currentTick;

				posStart = myTransform.position;
				rotStart = myTransform.rotation;
				startTime = Time.fixedTime;

				if (reconciliate) {
					tempResults = lastResults;
					Reconciliate ();
					lastResults = tempResults;
					reconciliate = false;
				}
					
				controller.enabled = true;
				lastResults = MoveCharacter (lastResults, clientInputs [clientInputs.Count - 1], Time.fixedDeltaTime * _sendUpdates, data.maxSpeedNormal);

				#if (CLIENT_TRUST)
				SendInputs (clientInputs [clientInputs.Count - 1], lastResults);
				#else
				SendInputs (clientInputs [clientInputs.Count - 1]);
				#endif
				if (data.debug && tickUpdateDebug != null)
					tickUpdateDebug(clientInputs [clientInputs.Count - 1], lastResults);

				controller.enabled = false;
				posEnd = lastResults.position;
				groundPointTime = lastResults.groundPointTime;
				posEndG = lastResults.groundPoint;
				rotEnd = lastResults.rotation;
			}

			if (isServer && currentFixedUpdates >= sendUpdates && (currentTFixedUpdates >= sendUpdates || isLocalPlayer)) {

				if (isLocalPlayer) {
					if (tickUpdate != null) tickUpdate (lastResults);
					sendResultsArray.Add(lastResults);
					SetDirtyBit (1);
				}

				if (!isLocalPlayer && clientInputs.Count > 0) {
					currentFixedUpdates -= sendUpdates;
					currentTFixedUpdates -= sendUpdates;
					//if (clientInputs.Count == 0)
					//	clientInputs.Add (curInputServer);
					//clientInputs[clientInputs.Count - 1] = curInputServer;
					curInput = SortClientInputsAndReturnFirst ();

					posStart = myTransform.position;
					rotStart = myTransform.rotation;
					startTime = Time.fixedTime;
					controller.enabled = true;
					serverResults = MoveCharacter (serverResults, curInput, Time.fixedDeltaTime * _sendUpdates, data.maxSpeedNormal);
					#if (CLIENT_TRUST)
					//if (serverResults.timestamp == tempResults.timestamp && Vector3.SqrMagnitude(serverResults.position-tempResults.position) <= data.clientPositionToleration * data.clientPositionToleration && Vector3.SqrMagnitude(serverResults.speed-tempResults.speed) <= data.clientSpeedToleration * data.clientSpeedToleration && ((serverResults.isGrounded == tempResults.isGrounded) || !data.clientGroundedMatch) && ((serverResults.crouch == tempResults.crouch) || !data.clientCrouchMatch))
						serverResults = tempResults;
					#endif
					groundPointTime = serverResults.groundPointTime;
					posEnd = serverResults.position;
					rotEnd = serverResults.rotation;
					posEndG = serverResults.groundPoint;
					controller.enabled = false;

					if (tickUpdate != null) tickUpdate (serverResults);
					if (data.debug && tickUpdateDebug != null)
						tickUpdateDebug(curInput, serverResults);
					sendResultsArray.Add(serverResults);
					SetDirtyBit (1);
				}

			}

			if (isLocalPlayer && currentFixedUpdates >= sendUpdates)
				currentFixedUpdates = 0;
		}

		//This is where all the interpolation happens
		void LateUpdate () {
			if (data.movementType == MoveType.UpdateOnceAndLerp) {
				if (isLocalPlayer || isServer || (Time.time - startTime) / (Time.fixedDeltaTime * _sendUpdates) <= 1f) {
					interpPos = Vector3.Lerp (posStart, posEnd, (Time.time - startTime) / (Time.fixedDeltaTime * _sendUpdates));
					//if ((Time.time - startTime) / (Time.fixedDeltaTime * _sendUpdates) <= groundPointTime)
					//	interpPos.y = Mathf.Lerp (posStart.y, posEndG, (Time.time - startTime) / (Time.fixedDeltaTime * _sendUpdates * groundPointTime));
					//else
					//	interpPos.y = Mathf.Lerp (posStart.y, posEndG, (Time.time - startTime + (groundPointTime * Time.fixedDeltaTime * _sendUpdates)) / (Time.fixedDeltaTime * _sendUpdates * (1f - groundPointTime)));

					myTransform.rotation = Quaternion.Lerp (rotStart, rotEnd, (Time.time - startTime) / (Time.fixedDeltaTime * _sendUpdates));
					if (isLocalPlayer)
						myTransform.rotation = Quaternion.Euler (myTransform.rotation.eulerAngles.x, curInput.x, myTransform.rotation.eulerAngles.z);
					myTransform.position = interpPos;
				} else {
					myTransform.position = Vector3.Lerp (posEnd, posEndO, (Time.time - startTime) / (Time.fixedDeltaTime * _sendUpdates) - 1f);
					myTransform.rotation = Quaternion.Lerp (rotEnd, rotEndO, (Time.time - startTime) / (Time.fixedDeltaTime * _sendUpdates) - 1f);
				}
			} else {
				myTransform.position = posEnd;
				myTransform.rotation = rotEnd;
			}

		}

		//Data not to be messed with. Needs to be outside the function due to OnControllerColliderHit
		Vector3 hitNormal;

		//Actual movement code. Mostly isolated, except transform
		Results MoveCharacter (Results inpRes, Inputs inp, float deltaMultiplier, Vector3 maxSpeed) {

			inp.y = Mathf.Clamp (curInput.y, dataInp.camMinY, dataInp.camMaxY);

			if (inp.x > 360f)
				inp.x -= 360f;
			else if (inp.x < 0f)
				inp.x += 360f;

			Vector3 pos = myTransform.position;
			Quaternion rot = myTransform.rotation;

			myTransform.position = inpRes.position;
			myTransform.rotation = inpRes.rotation;

			Vector3 tempSpeed = myTransform.InverseTransformDirection (inpRes.speed);

			myTransform.rotation = Quaternion.Euler (new Vector3 (0, inp.x, 0));

			//Character sliding of surfaces
			if (!inpRes.isGrounded) {
				inpRes.speed.x += (1f - inpRes.groundNormal.y) * inpRes.groundNormal.x * (inpRes.speed.y > 0 ? 0 : -inpRes.speed.y) * (1f - data.slideFriction);
				inpRes.speed.x += (1f - inpRes.groundNormal.y) * inpRes.groundNormal.x * (inpRes.speed.y < 0 ? 0 : inpRes.speed.y) * (1f - data.slideFriction);
				inpRes.speed.z += (1f - inpRes.groundNormal.y) * inpRes.groundNormal.z * (inpRes.speed.y > 0 ? 0 : -inpRes.speed.y) * (1f - data.slideFriction);
				inpRes.speed.z += (1f - inpRes.groundNormal.y) * inpRes.groundNormal.z * (inpRes.speed.y < 0 ? 0 : -inpRes.speed.y) * (1f - data.slideFriction);
			}

			Vector3 localSpeed = myTransform.InverseTransformDirection (inpRes.speed);
			Vector3 localSpeed2 = Vector3.Lerp (myTransform.InverseTransformDirection (inpRes.speed), tempSpeed, data.velocityTransferCurve.Evaluate (Mathf.Abs (inpRes.rotation.eulerAngles.y - inp.x) / (deltaMultiplier * data.velocityTransferDivisor)));

			if (!inpRes.isGrounded && data.strafing)
				AirStrafe (ref inpRes, ref inp, ref deltaMultiplier, ref maxSpeed, ref localSpeed, ref localSpeed2);
			else
				localSpeed = localSpeed2;

			BaseMovement (ref inpRes, ref inp, ref deltaMultiplier, ref maxSpeed, ref localSpeed);

			float tY = myTransform.position.y;

			inpRes.speed = transform.TransformDirection (localSpeed);
			hitNormal = new Vector3 (0, 0, 0);

			inpRes.speed.x = data.finalSpeedCurve.Evaluate (inpRes.speed.x);
			inpRes.speed.y = data.finalSpeedCurve.Evaluate (inpRes.speed.y);
			inpRes.speed.z = data.finalSpeedCurve.Evaluate (inpRes.speed.z);

			controller.Move (inpRes.speed * deltaMultiplier);
			//This code continues after OnControllerColliderHit gets called (if it does)

			if (Vector3.Angle (Vector3.up, hitNormal) <= data.slopeLimit)
				inpRes.isGrounded = true;
			else
				inpRes.isGrounded = false;

			float speed = inpRes.speed.y;
			inpRes.speed = (transform.position - inpRes.position) / deltaMultiplier;

			if (inpRes.speed.y > 0)
				inpRes.speed.y = Mathf.Min (inpRes.speed.y, Mathf.Max(0, speed));
			else
				inpRes.speed.y = Mathf.Max (inpRes.speed.y, Mathf.Min(0, speed));
			//inpRes.speed.y = speed;

			float gpt = 1f;
			float gp = myTransform.position.y;

			//WIP, broken, Handles hitting ground while spacebar is pressed. It determines how much time was left to move based on at which height the player hit the ground. Some math involved.
			if (data.handleMidTickJump && !inpRes.isGrounded && tY - gp >= 0 && inp.jump && (controller.isGrounded || Physics.Raycast (myTransform.position + controller.center, Vector3.down, (controller.height / 2) + (controller.skinWidth * 1.5f)))) {
				float oSpeed = inpRes.speed.y;
				gpt = (tY - gp) / (-oSpeed);
				inpRes.speed.y = data.speedJump + ((Physics.gravity.y / 2) * Mathf.Abs((1f - gpt) * deltaMultiplier));
				Debug.Log (inpRes.speed.y + " " + gpt);
				controller.Move (myTransform.TransformDirection (0, inpRes.speed.y * deltaMultiplier, 0));
				inpRes.isGrounded = true;
				Debug.DrawLine (new Vector3( myTransform.position.x, gp, myTransform.position.z), myTransform.position, Color.blue, deltaMultiplier);
				inpRes.jumped = true;
			}

			if (data.snapSize > 0f)
				myTransform.position = new Vector3 (Mathf.Round (myTransform.position.x * snapInvert) * data.snapSize, Mathf.Round (myTransform.position.y * snapInvert) * data.snapSize, Mathf.Round (myTransform.position.z * snapInvert) * data.snapSize);


			if (inpRes.isGrounded)
				localSpeed.y = Physics.gravity.y * Mathf.Clamp(deltaMultiplier, 1f, 1f);

			inpRes = new Results (myTransform.position, myTransform.rotation, hitNormal, inp.y, inpRes.speed, inpRes.isGrounded, inpRes.jumped, inpRes.crouch, gp, gpt, inp.timestamp);

			myTransform.position = pos;
			myTransform.rotation = rot;

			return inpRes;
		}

		//The part which determines if the controller was hit or not
		void OnControllerColliderHit (ControllerColliderHit hit) {
			hitNormal = hit.normal;
		}

		public void AirStrafe(ref Results inpRes, ref Inputs inp, ref float deltaMultiplier, ref Vector3 maxSpeed, ref Vector3 localSpeed, ref Vector3 localSpeed2) {
			if (inpRes.isGrounded)
				return;

			float tAccel = data.strafeAngleCurve.Evaluate(Mathf.Abs (inpRes.rotation.eulerAngles.y - inp.x) / deltaMultiplier);
			bool rDir = (inpRes.rotation.eulerAngles.y - inp.x) > 0;

			if (((inp.inputs.x > 0f && !rDir) || (inp.inputs.x < 0f && rDir)) && inp.inputs.y == 0) {
				if (localSpeed.z >= 0) {
					localSpeed.z = localSpeed2.z + tAccel * data.strafeToSpeedCurve.Evaluate (Mathf.Abs (localSpeed.z) * strafeToSpeedCurveScaleMul);
					localSpeed.x = localSpeed2.x;
					localSpeed.y = localSpeed2.y;
				} else
					localSpeed.z = localSpeed.z + tAccel * data.strafeToSpeedCurve.Evaluate (Mathf.Abs (localSpeed.z) * strafeToSpeedCurveScaleMul);
			} else if (((inp.inputs.x < 0f && !rDir) || inp.inputs.x > 0f && rDir) && inp.inputs.y == 0) {
				if (localSpeed.z <= 0) {
					localSpeed.z = localSpeed2.z - tAccel * data.strafeToSpeedCurve.Evaluate(Mathf.Abs(localSpeed.z) * strafeToSpeedCurveScaleMul);
					localSpeed.x = localSpeed2.x;
					localSpeed.y = localSpeed2.y;
				} else
					localSpeed.z = localSpeed.z - tAccel * data.strafeToSpeedCurve.Evaluate(Mathf.Abs(localSpeed.z) * strafeToSpeedCurveScaleMul);
			} else if (((inp.inputs.y > 0f && !rDir) || (inp.inputs.y < 0f && rDir)) && inp.inputs.x == 0) {
				if (localSpeed.x <= 0) {
					localSpeed.x = localSpeed2.x - tAccel * data.strafeToSpeedCurve.Evaluate(Mathf.Abs(localSpeed.x) * strafeToSpeedCurveScaleMul);
					localSpeed.z = localSpeed2.z;
					localSpeed.y = localSpeed2.y;
				} else
					localSpeed.x = localSpeed.x - tAccel * data.strafeToSpeedCurve.Evaluate(Mathf.Abs(localSpeed.x) * strafeToSpeedCurveScaleMul);
			} else if (((inp.inputs.y > 0f && rDir) || (inp.inputs.y < 0f && !rDir)) && inp.inputs.x == 0) {
				if (localSpeed.x >= 0) {
					localSpeed.x = localSpeed2.x + tAccel * data.strafeToSpeedCurve.Evaluate(Mathf.Abs(localSpeed.x) * strafeToSpeedCurveScaleMul);
					localSpeed.z = localSpeed2.z;
					localSpeed.y = localSpeed2.y;
				} else
					localSpeed.x = localSpeed.x + tAccel * data.strafeToSpeedCurve.Evaluate(Mathf.Abs(localSpeed.x) * strafeToSpeedCurveScaleMul);
			}
		}

		public void BaseMovement(ref Results inpRes, ref Inputs inp, ref float deltaMultiplier, ref Vector3 maxSpeed, ref Vector3 localSpeed) {
			
			if (inp.sprint)
				maxSpeed = data.maxSpeedSprint;
			if (inp.crouch) {
				maxSpeed = data.maxSpeedCrouch;
				if (!inpRes.crouch) {

					inpRes.crouch = true;

				}
				controller.height = Mathf.Clamp (controller.height - crouchSwitchMul, data.controllerHeightCrouch, data.controllerHeightNormal);
				controller.center = new Vector3 (0, controller.height * data.controllerCentreMultiplier, 0);
			} else {
				if (inpRes.crouch) {
					inpRes.crouch = false;

					Collider[] hits;

					hits = Physics.OverlapCapsule (inpRes.position + new Vector3(0f, data.controllerHeightCrouch, 0f), inpRes.position + new Vector3(0f, data.controllerHeightNormal, 0f), controller.radius);

					for (int i = 0; i < hits.Length; i++)
						if (hits [i].transform.root != myTransform.root) {
							inpRes.crouch = true;
							inp.crouch = true;
							maxSpeed = data.maxSpeedCrouch;
							break;
						}
				}
				if (!inpRes.crouch) {
					controller.height = Mathf.Clamp (controller.height + crouchSwitchMul, data.controllerHeightCrouch, data.controllerHeightNormal);
					controller.center = new Vector3 (0, controller.height * data.controllerCentreMultiplier, 0);
				} else {
					controller.height = data.controllerHeightCrouch;
					controller.center = new Vector3(0, controller.height * data.controllerCentreMultiplier, 0);
				}
			}

			if (!inp.jump)
				inpRes.jumped = false;

			if (inpRes.isGrounded && inp.jump && !inpRes.crouch && !inpRes.jumped) {
				localSpeed.y = data.speedJump;
				if (!data.allowBunnyhopping)
					inpRes.jumped = true;
			} else if (!inpRes.isGrounded)
				localSpeed.y += Physics.gravity.y * deltaMultiplier;
			else
				localSpeed.y = -1f;

			if (inpRes.isGrounded) {
				if (localSpeed.x >= 0f && inp.inputs.x > 0f) {
					localSpeed.x += data.accelerationSides * deltaMultiplier;
					if (localSpeed.x > maxSpeed.x)
						localSpeed.x = maxSpeed.x;
				} else if (localSpeed.x > 0f && (inp.inputs.x < 0f || localSpeed.x > maxSpeed.x)) {
					localSpeed.x -= data.accelerationStop * deltaMultiplier;
					if (localSpeed.x < 0)
						localSpeed.x = 0f;
				} else if (localSpeed.x <= 0f && inp.inputs.x < 0f) {
					localSpeed.x -= data.accelerationSides * deltaMultiplier;
					if (localSpeed.x < -maxSpeed.x)
						localSpeed.x = -maxSpeed.x;
				} else if (localSpeed.x < 0f && (inp.inputs.x > 0f || localSpeed.x < -maxSpeed.x)) {
					localSpeed.x += data.accelerationStop * deltaMultiplier;
					if (localSpeed.x > 0)
						localSpeed.x = 0f;
				} else if (localSpeed.x > 0f) {
					localSpeed.x -= data.decceleration * deltaMultiplier;
					if (localSpeed.x < 0f)
						localSpeed.x = 0f;
				} else if (localSpeed.x < 0f) {
					localSpeed.x += data.decceleration * deltaMultiplier;
					if (localSpeed.x > 0f)
						localSpeed.x = 0f;
				} else
					localSpeed.x = 0;

				if (localSpeed.z >= 0f && inp.inputs.y > 0f) {
					localSpeed.z += data.accelerationSides * deltaMultiplier;
					if (localSpeed.z > maxSpeed.z)
						localSpeed.z = maxSpeed.z;
				} else if (localSpeed.z > 0f && (inp.inputs.y < 0f || localSpeed.z > maxSpeed.z)) {
					localSpeed.z -= data.accelerationStop * deltaMultiplier;
					if (localSpeed.z < 0)
						localSpeed.z = 0f;
				} else if (localSpeed.z <= 0f && inp.inputs.y < 0f) {
					localSpeed.z -= data.accelerationSides * deltaMultiplier;
					if (localSpeed.z < -maxSpeed.z)
						localSpeed.z = -maxSpeed.z;
				} else if (localSpeed.z <= 0f && (inp.inputs.y > 0f || localSpeed.z < -maxSpeed.z)) {
					localSpeed.z += data.accelerationStop * deltaMultiplier;
					if (localSpeed.z > 0)
						localSpeed.z = 0f;
				} else if (localSpeed.z > 0f) {
					localSpeed.z -= data.decceleration * deltaMultiplier;
					if (localSpeed.z < 0f)
						localSpeed.z = 0f;
				} else if (localSpeed.z < 0f) {
					localSpeed.z += data.decceleration * deltaMultiplier;
					if (localSpeed.z > 0f)
						localSpeed.z = 0f;
				} else
					localSpeed.z = 0;
			}
		}
	}
}
