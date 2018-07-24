﻿using System.IO;

public static class Constants
{
    // Path.Combine is better because it supports cross-compatibility

    // "/ProfileData"
    public static readonly string EDITOR_SAVE_PATH = Path.Combine(Directory.GetCurrentDirectory(), "ProfileData");
    // "sdcard/PersonalRobotsGroup.FaceIDApp/ProfileData"
    public static readonly string ANDROID_SAVE_PATH = Path.Combine("sdcard", "PersonalRobotsGroup.FaceIDApp", "ProfileData");

    public static readonly string SAVE_PATH = GameController.DetermineSavePath();

    public static readonly string UNKNOWN_IMG_RSRC_PATH = Path.Combine("Stock Images", "unknown");
    public static readonly string SADFACE_IMG_RSRC_PATH = Path.Combine("Stock Images", "sad");

    public static readonly string INFO_FILE = "info.txt";

    public static readonly string API_ACCESS_KEY = GameController.DetermineAPIAccessKey();
    public static readonly string PERSON_GROUP_ID = "unity";
    public static readonly decimal CONFIDENCE_THRESHOLD = 0.70m;    // decimal between 0 and 1
    public static readonly int CAM_DELAY_MS = 2000;
    public static readonly int ROS_CONNECT_DELAY_MS = 1000;
    public static readonly string IMAGE_LABEL = "Image";
    public static readonly string IMAGE_DISPLAY_LABEL = "Photo";
    public static readonly string DELETED_IMG_LABEL = "deleted";

    public static readonly float FACEID_STATE_PUBLISH_HZ = 3.0f;
    public static readonly float FACEID_STATE_PUBLISH_DELAY_MS = 1000.0f / FACEID_STATE_PUBLISH_HZ;

    // face_msgs info
    public static readonly string FACE_MSGS_APP_NAME = "Face ID - Unity";
    public static readonly string FACE_MSGS_LOCATION = "eastus";


    // ROS connection information.
    public static readonly string DEFAULT_ROSBRIDGE_IP = "192.168.1.166";
    public static readonly string DEFAULT_ROSBRIDGE_PORT = "9090";

    // ROS topics.
    // FaceID to Roscore
    public static readonly string FACEID_EVENT_TOPIC = "/faceid_event";
    public static readonly string FACEID_EVENT_MESSAGE_TYPE = "/unity_game_msgs/FaceIDEvent";
    public static readonly string FACEID_STATE_TOPIC = "/faceid_state";
    public static readonly string FACEID_STATE_MESSAGE_TYPE = "/unity_game_msgs/FaceIDState";

    public static readonly string FACEAPIREQUEST_TOPIC = "/faceapi_requests";
    public static readonly string FACEAPIREQUEST_MESSAGE_TYPE = "/face_msgs/FaceAPIRequest";
    public static readonly string FACEAPIRESPONSE_TOPIC = "/faceapi_responses";
    public static readonly string FACEAPIRESPONSE_MESSAGE_TYPE = "/face_msgs/FaceAPIResponse";


    // Roscore to FaceID
    public static readonly string FACEID_COMMAND_TOPIC = "/faceid_command";
    public static readonly string FACEID_COMMAND_MESSAGE_TYPE = "/unity_game_msgs/FaceIDCommand";

    public static readonly string UNITY_ROSCONNECTION_SCENE = "ROS Connection";
    public static readonly string UNITY_GAME_SCENE = "Game";
}