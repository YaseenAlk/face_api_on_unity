﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityFaceIDHelper;
using MiniJSON;
using Newtonsoft.Json.Linq;

public class GameController : MonoBehaviour {
    
    // The singleton instance.
    public static GameController instance = null;

    // the task queue. Unfortunately, declaration + initialization is a bit verbose... :P
    private Queue<Tuple<Dictionary<string, object>, Func<Dictionary<string, object>, Task>>> taskQueue = new Queue<Tuple<Dictionary<string, object>, Func<Dictionary<string, object>, Task>>>();

    private RosManager rosManager;
    private UIAdjuster adjuster;
    private FaceAPIHelper apiHelper;

    private GameState currentState;
    private Dictionary<GameState, Func<Dictionary<string, object>, Task>> commands;


    private Profile loggedInProfile;   // the current logged in profile (nullable)

    private Profile selectedProfile;       // store profile that user has selected (nullable)
    private ProfileImage? selectedProfileImg;   // store image that user has selected (nullable)

    void Awake()
    {
        // Enforce singleton pattern.
        if (instance == null)
        {
            instance = this;
        }
        else if (instance != this)
        {
            Logger.Log("duplicate GameController, destroying");
            Destroy(gameObject);
        }
        DontDestroyOnLoad(gameObject);
    }

    // Use this for initialization
    void Start()
    {
        adjuster = UIAdjuster.instance;
        apiHelper = new FaceAPIHelper(Constants.API_ACCESS_KEY, Constants.PERSON_GROUP_ID);
        InitCommandDict();

        //SetState(GameState.GAMECONTROLLER_STARTING);

        //AddTask(GameState.STARTED);
        AddTask(GameState.ROS_CONNECTION);

        if (!Directory.Exists(Constants.SAVE_PATH))
        {
            Directory.CreateDirectory(Constants.SAVE_PATH);
        }
	}
	
	// Update is called once per frame
    // needs to be async because API calls are currently all marked with the async keyword
	async void Update()
    {
        //Logger.Log("Current gameState: " + this.GetGameState());
        await HandleTaskQueue();
	}

    // Handle main task queue.
    // needs to be async because API calls are currently all marked with the async keyword
    private async Task HandleTaskQueue()
    {
        // Pop tasks from the task queue and perform them.
        // Tasks are added from other threads, usually in response to ROS msgs.
        if (this.taskQueue.Count > 0)
        {
            try
            {
                Tuple<Dictionary<string, object>, Func<Dictionary<string, object>, Task>> task = this.taskQueue.Dequeue();
                Dictionary<string, object> parameters = task.Item1;
                Logger.Log("Got a task from queue in GameController: " + task.Item2.Method.Name);
                await task.Item2.Invoke(parameters);
            }
            catch (Exception e)
            {
                Logger.LogError("Error invoking Task on main thread!\n" + e);
            }
        }
    }


    private void InitCommandDict()
    {
        commands = new Dictionary<GameState, Func<Dictionary<string, object>, Task>>()
        {
            {GameState.ROS_CONNECTION, this.OpenROSConnectScreen()},

            {GameState.ROS_HELLO_WORLD_ACK, this.ROSHelloWorldAck()},

            {GameState.STARTED, this.StartGame()},
            {GameState.NEW_PROFILE_PROMPT, this.AskNewProfile()},
            {GameState.MUST_LOGIN_PROMPT, this.ShowMustLogin()},
            {GameState.ENTER_NAME_PROMPT, this.AskForNewProfileName()},
            {GameState.EVALUATING_TYPED_NAME, this.EvaluateTypedNameAsync()},
            {GameState.LISTING_IMAGES, this.ShowPicturesForProfile()},
            {GameState.TAKING_WEBCAM_PIC, this.OpenWebcamForPictureAsync()},
            {GameState.CHECKING_TAKEN_PIC, this.CheckPictureTakenAsync()},
            {GameState.PIC_APPROVAL, this.ShowImgApprovalPage()},
            {GameState.PIC_DISAPPROVAL, this.ShowImgDisapprovalPage()},
            {GameState.SAVING_PIC, this.AddImgToProfileAsync()},
            {GameState.LISTING_PROFILES, this.ListProfiles()},
            {GameState.LOGIN_DOUBLE_CHECK, this.ShowLoginDoubleCheck()},
            {GameState.LOGGING_IN, this.LogIn()},
            {GameState.CANCELLING_LOGIN, this.CancelLogin()},
            {GameState.WELCOME_SCREEN, this.ShowWelcomeScreen()},
            {GameState.SHOWING_SELECTED_PHOTO, this.ShowSelectedPhoto()},
            {GameState.DELETING_PHOTO, this.DeletePhotoAsync()},
            {GameState.REJECTION_PROMPT, this.ShowRejectionPrompt()},

            {GameState.API_ERROR_CREATE, this.APIError(GameState.API_ERROR_CREATE, "(during LargePersonGroup Person creation)")},
            {GameState.API_ERROR_COUNTING_FACES, this.APIError(GameState.API_ERROR_COUNTING_FACES, "(while counting faces)")},
            {GameState.API_ERROR_ADDING_FACE, this.APIError(GameState.API_ERROR_ADDING_FACE, "(while adding a face)")},
            {GameState.API_ERROR_IDENTIFYING, this.APIError(GameState.API_ERROR_IDENTIFYING, "(while identifying)")},
            {GameState.API_ERROR_GET_NAME, this.APIError(GameState.API_ERROR_GET_NAME, "(while trying to get name from ID after auth fail)")},
            {GameState.API_ERROR_TRAINING_STATUS, this.APIError(GameState.API_ERROR_TRAINING_STATUS, "(while checking training status)")},
            {GameState.API_ERROR_DELETING_FACE, this.APIError(GameState.API_ERROR_DELETING_FACE, "(while deleting a face)")},

            {GameState.INTERNAL_ERROR_PARSING, this.InternalError(GameState.INTERNAL_ERROR_PARSING, "(while parsing Task parameters)")},
            {GameState.INTERNAL_ERROR_NAME_FROM_ID, this.InternalError(GameState.INTERNAL_ERROR_NAME_FROM_ID, "(while retrieving name for personId locally)")}

        };
    }

    public void AddTask(GameState state, Dictionary<string, object> properties = null)
    {
        if (commands.ContainsKey(state))
        {
            Func<Dictionary<string, object>, Task> task = commands[state];
            Tuple<Dictionary<string, object>, Func<Dictionary<string, object>, Task>> toQueue = new Tuple<Dictionary<string, object>, Func<Dictionary<string, object>, Task>>(properties, task);
            this.taskQueue.Enqueue(toQueue);
        }
        else
        {
            Logger.LogError("Unknown GameState Task! state = " + state);
        }
    }

    // Clean up.
    void OnApplicationQuit()
    {
        if (this.rosManager != null && this.rosManager.IsConnected())
        {
            // Stop the thread that's sending StorybookState messages.
            this.rosManager.StopSendingFaceIDState();
            // Close the ROS connection cleanly.
            this.rosManager.CloseConnection();
        }
    }

    // don't be intimidated by the method type!
    // each of these Funcs take in a Dictionary of parameters (nullable)
    // and returns a Task that (may or may not) use the parameters

    private Func<Dictionary<string, object>, Task> OpenROSConnectScreen()
    {
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.ROS_CONNECTION);
            ClearQueuedData(); // there shouldn't be any, but just in case...
            adjuster.HideAllElementsAction();
            SceneManager.LoadScene(Constants.UNITY_ROSCONNECTION_SCENE, LoadSceneMode.Single);
        };
    }

    private Func<Dictionary<string, object>, Task> StartGame()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.STARTED);
            ClearQueuedData();
            adjuster.AskQuestionAction("\r\nHi! Are you new here?");
        };
    }

    private Func<Dictionary<string, object>, Task> AskNewProfile()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.NEW_PROFILE_PROMPT);
            adjuster.AskQuestionAction("\r\nWould you like to make a profile?");
        };
    }

    private Func<Dictionary<string, object>, Task> ShowMustLogin()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.MUST_LOGIN_PROMPT);
            adjuster.PromptOKDialogueAction("\r\nIn order to use the app, you must be logged into a profile.");
        };
    }

    private Func<Dictionary<string, object>, Task> AskForNewProfileName()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.ENTER_NAME_PROMPT);
            adjuster.PromptInputTextAction("What is your name?\r\n\r\nPlease ensure that the name you enter is valid.");
        };

    }

    private Func<Dictionary<string, object>, Task> EvaluateTypedNameAsync()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.EVALUATING_TYPED_NAME);

            string typedName;

            bool parsed = TryParseParam("typedName", parameters, out typedName);

            if (parsed)
            {
                if (IsInvalidName(typedName))  // conditions for an invalid name
                    AddTask(GameState.ENTER_NAME_PROMPT);
                else
                {
                    adjuster.PromptNoButtonPopUpAction("Hold on, I'm thinking... (creating LargePersonGroup Person)");
                    FaceAPICall<string> apiCall = apiHelper.CreateLargePersonGroupPersonCall(typedName);
                    await MakeRequestAndSendInfoToROS(apiCall);
                    string personID = apiCall.GetResult();
                    if (apiCall.SuccessfulCall() && personID != "") //successful API call
                    {
                        CreateProfile(typedName, personID, true);
                    }
                    else
                    { // maybe internet is down, maybe api access is revoked...
                        AddTask(GameState.API_ERROR_CREATE);
                        Logger.LogError("API Error occurred while trying to create a LargePersonGroup Person");
                    }
                }
            }
        };
    }

    private Func<Dictionary<string, object>, Task> ShowPicturesForProfile()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.LISTING_IMAGES);
            List<ProfileImage> imageList = loggedInProfile.images;
            adjuster.ListImagesAction("Here is your photo listing:", imageList);
        };
    }

    private Func<Dictionary<string, object>, Task> OpenWebcamForPictureAsync()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.TAKING_WEBCAM_PIC);
            await AuthenticateIfNecessaryThenDo(() =>
            {
                adjuster.ShowWebcamAction("Take a picture!", "Snap!");
            }, loggedInProfile, true);
        };
    }

    private Func<Dictionary<string, object>, Task> CheckPictureTakenAsync()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.CHECKING_TAKEN_PIC);

            Sprite frame;
            bool parsed = TryParseParam("photo", parameters, out frame);

            if (parsed)
            {
                adjuster.PromptNoButtonPopUpAction("Hold on, I'm thinking... (counting faces in image)");
                byte[] imgData = frame.texture.EncodeToPNG();

                FaceAPICall<int> apiCall = apiHelper.CountFacesCall(imgData);
                await MakeRequestAndSendInfoToROS(apiCall);

                int numFaces = apiCall.GetResult();
                if (!apiCall.SuccessfulCall() || numFaces == -1)
                {
                    AddTask(GameState.API_ERROR_COUNTING_FACES);
                    Logger.LogError("API Error occurred while trying to count the faces in a frame");
                    return;
                }

                if (numFaces < 1)   //pic has no detectable faces in it... try again.
                    AddTask(GameState.PIC_DISAPPROVAL);
                else
                {
                    await AuthenticateIfNecessaryThenDo(() =>
                    {
                        Dictionary<string, object> param = new Dictionary<string, object>
                        {
                            { "savedFrame", frame }
                        };

                        AddTask(GameState.PIC_APPROVAL, param);

                    }, loggedInProfile, true, frame);
                }
            }
        };
    }

    private Func<Dictionary<string, object>, Task> ShowImgDisapprovalPage()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.PIC_DISAPPROVAL);
            adjuster.PicWindowAction(SadFaceSprite(), "I didn't like this picture :( Can we try again?", "Try again...", "Cancel");
        };
    }

    private Func<Dictionary<string, object>, Task> ShowImgApprovalPage()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.PIC_APPROVAL);

            Sprite frame;
            bool parsed = TryParseParam("savedFrame", parameters, out frame);

            if (parsed)
            {
                adjuster.PicWindowAction(frame, "I like it! What do you think?", "Keep it!", "Try again...");
            }

        };
    }

    private Func<Dictionary<string, object>, Task> AddImgToProfileAsync()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.SAVING_PIC);

            Sprite frame;
            bool parsed = TryParseParam("photo", parameters, out frame);

            if (parsed)
            {
                bool success = await AddImgToProfile(loggedInProfile, frame);
                if (success)
                {
                    ReloadProfile();
                    AddTask(GameState.LISTING_IMAGES);
                }
            }

        };
    }

    private Func<Dictionary<string, object>, Task> ListProfiles()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.LISTING_PROFILES);
            List<Profile> profiles = LoadProfiles();
            adjuster.ListProfilesAction("Here are the existing profiles:", profiles);
        };
    }

    private Func<Dictionary<string, object>, Task> ShowLoginDoubleCheck()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.LOGIN_DOUBLE_CHECK);

            Profile attempt;
            bool parsed = TryParseParam("attemptedLogin", parameters, out attempt);

            if (parsed)
            {
                Sprite pic = ImgDirToSprite(attempt.profilePicture);

                this.selectedProfile = attempt;

                adjuster.PicWindowAction(pic, "Are you sure you want to log in as " + attempt.displayName + "?", "Login", "Back");
            }

        };
    }

    private Func<Dictionary<string, object>, Task> LogIn()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.LOGGING_IN);

            Profile profile;
            bool parsed = TryParseParam("profile", parameters, out profile);

            if (parsed)
            {
                bool success = await AuthenticateIfNecessaryThenDo(() =>
                {
                    this.loggedInProfile = profile;
                    AddTask(GameState.WELCOME_SCREEN);
                }, profile, true);
            }

        };
    }

    private Func<Dictionary<string, object>, Task> CancelLogin()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.CANCELLING_LOGIN);
            ClearQueuedData();
            AddTask(GameState.LISTING_PROFILES);
        };
    }

    private Func<Dictionary<string, object>, Task> ShowRejectionPrompt()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.REJECTION_PROMPT);

            string attemptedName;
            Dictionary<string, decimal> guesses;
            bool parsedName = TryParseParam("name", parameters, out attemptedName);
            bool parsedGuesses = TryParseParam("guesses", parameters, out guesses);

            if (parsedName && parsedGuesses)
            {
                string response = "Are you sure you're " + attemptedName + "? Because I'm";

                if (guesses.Count == 0)
                    response += " not sure who you are, to be honest.";
                else
                {
                    adjuster.PromptNoButtonPopUpAction("Hold on, I'm thinking... (retrieving name(s) from personId(s))");
                    for (int i = 0; i < guesses.Count; i++)
                    {
                        string key = guesses.Keys.ElementAt(i);
                        string nameFromID = GetNameFromIDLocal(key);
                        if (nameFromID == "")
                        {
                            AddTask(GameState.INTERNAL_ERROR_NAME_FROM_ID);
                            Logger.LogError("Internal Error occurred while trying to get name from ID (after auth fail)");
                        }

                        if ((i + 1) == guesses.Count && guesses.Count > 1)
                            response += " and";

                        response += " " + (guesses[key] * 100) + "% sure you are " + nameFromID;

                        if ((i + 1) < guesses.Count)
                            response += ",";
                    }
                }
                adjuster.PromptOKDialogueAction(response);
            }

        };
    }

    // safe to assume that user has logged in (?)
    private Func<Dictionary<string, object>, Task> ShowWelcomeScreen()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.WELCOME_SCREEN);
            adjuster.PromptOKDialogueAction("\r\nWelcome, " + loggedInProfile.displayName + "!");
        };
    }

    private Func<Dictionary<string, object>, Task> ShowSelectedPhoto()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.SHOWING_SELECTED_PHOTO);

            ProfileImage img;
            bool parsed = TryParseParam("profileImg", parameters, out img);

            if (parsed)
            {
                Sprite photo = ImgDirToSprite(img.path);

                this.selectedProfileImg = img;

                adjuster.PicWindowAction(photo, "Nice picture! What would you like to do with it?", "Delete it", "Keep it");
            }

        };
    }

    private Func<Dictionary<string, object>, Task> DeletePhotoAsync()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(GameState.DELETING_PHOTO);

            ProfileImage img;
            bool parsed = TryParseParam("profileImg", parameters, out img);

            if (parsed)
            {
                await AuthenticateIfNecessaryThenDo(async () =>
                {
                    bool success = await DeleteSelectedPhoto(loggedInProfile, img);
                    if (success)
                    {
                        ReloadProfile();
                        AddTask(GameState.LISTING_IMAGES);
                    }
                }, loggedInProfile, true);
            }

        };
    }

    private Func<Dictionary<string, object>, Task> APIError(GameState newState, string err)
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(newState);
            adjuster.PromptOKDialogueAction("API Error\r\n" + err);
        };
    }

    // might combine with above function in the future
    private Func<Dictionary<string, object>, Task> InternalError(GameState newState, string err)
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            SetState(newState);
            adjuster.PromptOKDialogueAction("Internal Error\r\n" + err);
        };
    }

    private async Task<bool> AuthenticateIfNecessaryThenDo(Action f, Profile profile, bool showRejectionPrompt, Sprite imgToCheck = null)
    {
        if (!ShouldBeAuthenticated(profile))
        {
            f();
            return true;
        }
        else
        {
            bool verified = await VerifyAsync(profile, showRejectionPrompt, imgToCheck);
            if (verified)
            {
                f();
            }
            return verified;
        }
    }

    // TODO: find a better way to do this
    private bool TryParseParam<T>(string param, Dictionary<string, object> dict, out T obj, bool throwError=true)
    {
        try
        {
            if (typeof(T).IsValueType)
            {
                obj = (T)dict[param];
            }
            else
            {
                obj = (T)(object)dict[param];
            }

            return true;
        }
        catch (Exception e)
        {
            obj = default(T);
            if (throwError)
            {
                Logger.LogError("[parameter parsing] Parsing " + param + " to " + typeof(T) + ": \r\n" + e.ToString());
                AddTask(GameState.INTERNAL_ERROR_PARSING);
            }
            return false;
        }
    }

    private void SetState(GameState newState)
    {
        currentState = newState;
    }

    public GameState GetGameState()
    {
        return currentState;
    }

    private void CreateProfile(string displayName, string personId, bool loginAfterward = true)
    {
        string folderName = displayName;
        if (Directory.Exists(Path.Combine(Constants.SAVE_PATH, displayName)))
        {
            int count = Directory.GetDirectories(Constants.SAVE_PATH, displayName + "*").Length;
            folderName = displayName + " (" + count + ")";
        }
        Directory.CreateDirectory(Path.Combine(Constants.SAVE_PATH, folderName));

        Profile newPerson = new Profile
        {
            displayName = displayName,
            folderName = folderName,
            imageCount = 0,
            images = new List<ProfileImage>(),
            personId = personId,
            profilePicture = "none"
        };

        ExportProfileInfo(newPerson);

        if (loginAfterward)
        {
            Dictionary<string, object> param = new Dictionary<string, object>
            {
                { "profile", newPerson }
            };
            AddTask(GameState.LOGGING_IN, param);
        }

    }
    
    private void ExportProfileInfo(Profile p)
    {
        Logger.Log("Exporting the following profile:\r\n" + p.ToString());
        Dictionary<string, object> json = new Dictionary<string, object>();

        json.Add("personId", p.personId);
        json.Add("displayName", p.displayName);
        json.Add("count", p.imageCount);
        json.Add("profilePic", p.profilePicture ?? "none");

        ProfileImage?[] imgList = new ProfileImage?[p.imageCount];

        Logger.Log("p.images.Count: " + p.images.Count);

        foreach (ProfileImage prof in p.images)
        {
            int index = prof.indexNumber;
            imgList[index] = prof;
        }

        Dictionary<string, object> images = new Dictionary<string, object>();

        for (int i = 0; i < imgList.Length; i++)
        {
            ProfileImage? data = imgList[i];

            if (data == null)
            {
                images.Add(Constants.IMAGE_LABEL + " " + i, "deleted");
                continue;
            }
            else
            {
                Dictionary<string, object> imgData = new Dictionary<string, object>();
                imgData.Add("path", data.Value.path);
                imgData.Add("persistedFaceId", data.Value.persistedFaceId);

                images.Add(Constants.IMAGE_LABEL + " " + i, imgData);
            }
        }

        json.Add("images", images);

        string savePath = Path.Combine(Constants.SAVE_PATH, p.folderName, Constants.INFO_FILE);
        string unformatted = Json.Serialize(json);
        string dataToSave = JToken.Parse(unformatted).ToString(Formatting.Indented);
        System.IO.File.WriteAllText(savePath, dataToSave);
    }

    private async Task<bool> AddImgToProfile(Profile profile, Sprite img)
    {
        adjuster.PromptNoButtonPopUpAction("Hold on, I'm thinking... (adding Face to LargePersonGroup Person)");
        Texture2D tex = img.texture;
        byte[] imgData = tex.EncodeToPNG();

        FaceAPICall<string> call = apiHelper.AddFaceToLargePersonGroupPersonCall(profile.personId, imgData);

        await MakeRequestAndSendInfoToROS(call);

        string persistedId = call.GetResult();

        if (!call.SuccessfulCall() || persistedId == "")
        {
            AddTask(GameState.API_ERROR_ADDING_FACE);
            Logger.LogError("API Error while trying to add Face to LargePersonGroup Person");
            return false;
        }

        int count = profile.imageCount;

        ProfileImage newImg = new ProfileImage
        {
            imageOwner = profile,
            indexNumber = count,
            number = profile.images.Count,
            path = Path.Combine(Constants.SAVE_PATH, profile.folderName, Constants.IMAGE_LABEL + " " + count + ".png"),
            persistedFaceId = persistedId
        };

        System.IO.File.WriteAllBytes(newImg.path, tex.EncodeToPNG());
        Logger.Log("count = " + count);
        profile.imageCount = count + 1;
        Logger.Log("count now = " + profile.imageCount);

        List<ProfileImage> newImgList = profile.images;
        newImgList.Add(newImg);

        profile.images = newImgList;

        ExportProfileInfo(profile);
        return await RetrainProfilesAsync();
    }

    private List<Profile> LoadProfiles()
    {
        try
        {
            List<Profile> profiles = new List<Profile>();

            string[] profileDirs = Directory.GetDirectories(Constants.SAVE_PATH);
            foreach (string dir in profileDirs)
            {
                if (File.Exists(Path.Combine(dir, Constants.INFO_FILE)))
                {
                    string folderName = new DirectoryInfo(dir).Name;

                    Profile attemptToLoad = LoadProfileData(folderName);
                    if (attemptToLoad != null)
                        profiles.Add(attemptToLoad);
                }
            }

            return profiles;
        }
        catch (Exception e)
        {
            Logger.LogError("[profile list loading]: " + e.ToString());
            return new List<Profile>();
        }

    }

    private string GetProfilePicDir(string fName, int unknown = 0)
    {
        string dir;

        string[] profileImgDirs = Directory.GetFiles(Path.Combine(Constants.SAVE_PATH, fName), "*.png");
        if (profileImgDirs.Length < 1)
        {
            dir = "(" + unknown + ") " + Constants.UNKNOWN_IMG_RSRC_PATH;
            unknown++;
        }
        else
            dir = profileImgDirs[0];   //could also be a random photo, or maybe in the future they can pick a "profile pic"

        return dir;
    }

    private void ClearQueuedData()
    {
        if (loggedInProfile != null)
            ExportProfileInfo(loggedInProfile);
        
        loggedInProfile = null;
        selectedProfile = null;
        selectedProfileImg = null;
    }

    public void SelectProfile(Profile profile)
    {
        Dictionary<string, object> parameter = new Dictionary<string, object>
        {
            { "attemptedLogin", profile }
        };

        AddTask(GameState.LOGIN_DOUBLE_CHECK, parameter);
    }

    private Profile LoadProfileData(string folderName)
    {
        try
        {
            Profile newProf = new Profile();

            Dictionary<string, object> data = LoadDataFile(folderName);
            newProf.displayName = data["displayName"].ToString();
            newProf.folderName = folderName;
            newProf.imageCount = Int32.Parse(data["count"].ToString());
            newProf.personId = data["personId"].ToString();
            newProf.profilePicture = data["profilePic"].ToString();

            Dictionary<string, object> images = ((JObject)data["images"]).ToObject<Dictionary<string, object>>();

            Logger.Log("images null: " + (images == null));

            List<ProfileImage> profileImgs = LoadProfileImageData(newProf, images);

            if (profileImgs == null)
                throw new JsonException("profileImgs is null -- error processing image JSON data?");

            newProf.images = profileImgs;
            return newProf;
        }
        catch (Exception e)
        {
            Logger.LogError("[data loading] " + e.ToString());
            return null;
        }
    }

    private List<ProfileImage> LoadProfileImageData(Profile p, Dictionary<string, object> data)
    {
        List<ProfileImage> profileImgs;
        try
        {
            profileImgs = new List<ProfileImage>();
            foreach (KeyValuePair<string, object> entry in data)
            {
                if (entry.Value.ToString() == "deleted")
                    continue;
                
                Dictionary<string, object> info = ((JObject)entry.Value).ToObject<Dictionary<string, object>>();

                string prefix = Constants.IMAGE_LABEL + " ";
                int num = Int32.Parse(entry.Key.Substring(prefix.Length));

                string path = (string)info["path"];
                string persistedFaceId = (string)info["persistedFaceId"];

                ProfileImage pImg = new ProfileImage
                {
                    imageOwner = p,
                    indexNumber = num,
                    number = profileImgs.Count,
                    path = path,
                    persistedFaceId = persistedFaceId
                };

                profileImgs.Add(pImg);

            }
        }
        catch (Exception e)
        {
            profileImgs = null;
            Logger.LogError("[data loading] " + e.ToString());
        }

        return profileImgs;
    }

    private string FolderNameToLoginName(string fName)
    {
        string pName = fName;
        int index = fName.IndexOf('(');
        if (index > 0)
            pName = fName.Substring(0, index - 1);
        return pName;
    }

    private Dictionary<string, object> LoadDataFile(string folder)
    {
        var filePath = Path.Combine(Constants.SAVE_PATH, folder, Constants.INFO_FILE);
        string json = System.IO.File.ReadAllText(filePath);
        return JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
    }

    private Dictionary<string, string> ReadJsonDictFromFile(string path)
    {
        string json = System.IO.File.ReadAllText(path);
        Dictionary<string, string> data = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
        return data;
    }

    public void SelectPhoto(ProfileImage img)
    {
        Dictionary<string, object> parameters = new Dictionary<string, object>
        {
            { "profileImg", img }
        };

        AddTask(GameState.SHOWING_SELECTED_PHOTO, parameters);
    }

    private async Task<bool> DeleteSelectedPhoto(Profile person, ProfileImage photo)
    {
        string fileName = Path.GetFileNameWithoutExtension(photo.path);
        string persistedId = photo.persistedFaceId;

        adjuster.PromptNoButtonPopUpAction("Hold on, I'm thinking... (deleting Face from LargePersonGroup Person)");

        FaceAPICall<bool> apiCall = apiHelper.DeleteFaceFromLargePersonGroupPersonCall(person.personId, persistedId);
        await MakeRequestAndSendInfoToROS(apiCall);

        bool deleted = apiCall.GetResult();

        if (apiCall.SuccessfulCall() && deleted)
        {
            person.images.Remove(photo);
            ExportProfileInfo(person);
            File.Delete(photo.path);
            return await RetrainProfilesAsync();
        }
        else
        {
            AddTask(GameState.API_ERROR_DELETING_FACE);
            Logger.LogError("API Error while trying to delete Face from LargePersonGroup Person");
            return false;
        }
    }

    // currently, face API verification isn't needed unless a profile has at least 5 pics
    // (the less pictures that the API is trained with, the less accurate its verifications will be)
    private bool ShouldBeAuthenticated(Profile profile)
    {
        return profile.images.Count >= 5;
    }

    private async Task<bool> VerifyAsync(Profile attempt, bool showRejectionPrompt = false, Sprite imgToCheck = null)
    {
        adjuster.HideAllElementsAction();
        Sprite frame;

        if (imgToCheck == null)
        {
            adjuster.EnableCamera();
            await Task.Delay(Constants.CAM_DELAY_MS); //add delay so that the camera can turn on and focus
            adjuster.GrabCurrentWebcamFrame();
            frame = adjuster.GetCurrentSavedFrame();
        }
        else
            frame = imgToCheck;

        adjuster.PromptNoButtonPopUpAction("Hold on, I'm thinking... (identifying faces in current frame)");
        byte[] frameData = frame.texture.EncodeToPNG();

        // 2 calls: Face - Detect and then Face - Identify

        FaceAPICall<List<string>> detectAPICall = apiHelper.DetectForIdentifyingCall(frameData);
        await MakeRequestAndSendInfoToROS(detectAPICall);
        List<string> faceIds = detectAPICall.GetResult();

        if (!detectAPICall.SuccessfulCall() || faceIds == null)
        {
            AddTask(GameState.API_ERROR_IDENTIFYING);
            Logger.LogError("API Error occurred while trying to identify LargePersonGroup Person in a frame");
            return false;
        }

        string biggestFaceId = faceIds[0];

        FaceAPICall<Dictionary<string, decimal>> identifyAPICall = apiHelper.IdentifyFromFaceIdCall(biggestFaceId);
        await MakeRequestAndSendInfoToROS(identifyAPICall);

        Dictionary<string, decimal> guesses = identifyAPICall.GetResult();

        if (!identifyAPICall.SuccessfulCall() || guesses == null)
        {
            AddTask(GameState.API_ERROR_IDENTIFYING);
            Logger.LogError("API Error occurred while trying to identify LargePersonGroup Person in a frame");
            return false;
        }

        bool verified = AuthenticateLogin(attempt.personId, guesses);
        if (verified)  //identified and above confidence threshold
        {
            return true;
        }
        else
        {
            if (showRejectionPrompt)
            {
                Dictionary<string, object> parameters = new Dictionary<string, object>
                {
                    { "name", attempt.displayName },
                    { "guesses", guesses }
                };

                AddTask(GameState.REJECTION_PROMPT, parameters);
            }
            return false;
        }
    }

    private bool AuthenticateLogin(string personId, Dictionary<string, decimal> guesses)
    {
        if (!guesses.ContainsKey(personId))
        {
            Logger.Log("guesses does not contain the personId");
            return false;
        }
        else
        {
            decimal confidence = guesses[personId];
            return confidence >= Constants.CONFIDENCE_THRESHOLD;
        }
    }

    private async Task<bool> RetrainProfilesAsync()
    {
        adjuster.PromptNoButtonPopUpAction("Hold on, I'm thinking... (re-training profiles)");
        FaceAPICall<bool> startTrainingAPICall = apiHelper.StartTrainingLargePersonGroupCall();
        await MakeRequestAndSendInfoToROS(startTrainingAPICall);

        FaceAPICall<string> trainingStatusAPICall = apiHelper.GetLargePersonGroupTrainingStatusCall();
        await MakeRequestAndSendInfoToROS(trainingStatusAPICall);

        string status = trainingStatusAPICall.GetResult();
        while (status != FaceAPIHelper.TRAINING_SUCCEEDED)
        {
            if (status == FaceAPIHelper.TRAINING_FAILED || status == FaceAPIHelper.TRAINING_API_ERROR || !trainingStatusAPICall.SuccessfulCall()) {
                AddTask(GameState.API_ERROR_TRAINING_STATUS);
                Logger.LogError("API Error occurred when checking training status");
                return false;
            }
            Logger.Log("Checking training status...");
            trainingStatusAPICall = apiHelper.GetLargePersonGroupTrainingStatusCall();
            await MakeRequestAndSendInfoToROS(trainingStatusAPICall);
            status = trainingStatusAPICall.GetResult();
            Logger.Log("status = " + status);
        }
        return true;
    }

    private bool IsInvalidName(string nameToTest) 
    {
        if (nameToTest.Length < 2 || nameToTest.Length > 50)  // limit is technically 256 characters anything over 31 seems unnecessarily long
            return true;

        char[] alphabet = {'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 
            'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 
            'x', 'y', 'z', ' '};

        bool onlySpaces = true;

        foreach (char c in nameToTest.ToLower())
        {
            if (!alphabet.Contains(c))
                return true;

            if (c != ' ')
                onlySpaces = false;
        }

        return onlySpaces || (nameToTest[nameToTest.Length - 1] == ' ');
    }

    private byte[] GetImageAsByteArray(string imageFilePath)
    {
        using (FileStream fileStream =
            new FileStream(imageFilePath, FileMode.Open, FileAccess.Read))
        {
            BinaryReader binaryReader = new BinaryReader(fileStream);
            return binaryReader.ReadBytes((int)fileStream.Length);
        }
    }

    private Sprite ImgDirToSprite(string dir)
    {
        // Create a texture. Texture size does not matter, since
        // LoadImage will replace with with incoming image size.
        Texture2D tex;

        if (dir.Contains("none"))
        {
            tex = Resources.Load(Constants.UNKNOWN_IMG_RSRC_PATH) as Texture2D;
        }
        else
        {
            tex = new Texture2D(2, 2);
            byte[] pngBytes = GetImageAsByteArray(dir);
            // Load data into the texture.
            tex.LoadImage(pngBytes);
        }

        Sprite newImage = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0, 0));
        return newImage;
    }

    private Sprite SadFaceSprite()
    {
        Texture2D tex = Resources.Load(Constants.SADFACE_IMG_RSRC_PATH) as Texture2D;
        Sprite newImage = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0, 0));
        return newImage;
    }

    public static string DetermineSavePath()
    {
        string ret = Constants.EDITOR_SAVE_PATH;

        #if UNITY_ANDROID
        Debug.Log("Unity Android Detected");
        ret = Constants.ANDROID_SAVE_PATH;
        #endif

        return ret;
    }

    public static string DetermineAPIAccessKey()
    {
        TextAsset api_access_key = Resources.Load("api_access_key") as TextAsset;
        return api_access_key.text;
    }

    public RosManager GetRosManager()
    {
        return this.rosManager;
    }

    public void SetRosManager(RosManager newRos)
    {
        this.rosManager = newRos;
    }

    public Profile GetSelectedProfile()
    {
        return this.selectedProfile;
    }

    public ProfileImage GetSelectedProfileImage()
    {
        return this.selectedProfileImg.Value;
    }

    private string GetNameFromIDLocal(string personId)
    {
        List<Profile> profiles = LoadProfiles();
        foreach (Profile person in profiles)
        {
            if (person.personId.ToLower() == personId.ToLower())
                return person.displayName;
        }
        return "";  //  ??? unknown id???
    }

    private void ReloadProfile()
    {
        this.loggedInProfile = LoadProfileData(loggedInProfile.folderName);
    }

    // ====================================================================
    // All ROS message handlers.
    // They should add tasks to the task queue, because many of their
    // functionalities will throw errors if not run on the main thread.
    // ====================================================================

    public void RegisterRosMessageHandlers()
    {
        this.rosManager.RegisterHandler(FaceIDCommand.HELLO_WORLD_ACK, GameState.ROS_HELLO_WORLD_ACK);
    }

    // HELLO_WORLD_ACK
    private void OnHelloWorldAckReceived(Dictionary<string, object> args)
    {
        Logger.Log("OnHelloWorldAckReceived");
        AddTask(GameState.ROS_HELLO_WORLD_ACK);
    }

    private Func<Dictionary<string, object>, Task> ROSHelloWorldAck()
    {
        
        return async (Dictionary<string, object> parameters) =>
        {
            ConnectionScreenController.instance.ShowContinueButton();
            //Logger.Log("Our \"Hello World\" ping has been received and acknowledged by the ROS Gods! :D");
            //SetState(GameState.ROS_HELLO_WORLD_ACK);
            //SceneManager.LoadScene("Game", LoadSceneMode.Single);
            //await Task.Delay(2000);
            //adjuster.HideAllElements();
            //adjuster.PromptOKDialogue("Our \"Hello World\" ping has been received and acknowledged by the ROS Gods! :D");
        };
    }

    // class so that it gets passed by reference
    public class Profile : ProfileHandler.IScrollable
    {
        public string displayName, folderName;

        // index: image number
        // string 1: image path
        // string 2: Face API persistedFaceId
        public List<ProfileImage> images;

        public string personId;

        public int imageCount;

        public string profilePicture;   // stored as a path

        public string ImgPath { get { return profilePicture; } }

        public string DisplayName { get { return displayName; } }

        public string IdentifyingName { get { return folderName; } }

        public override string ToString()
        {
            string ret = "";
            ret += "Display Name: " + displayName;
            ret += "\r\nFolder Name: " + folderName;
            ret += "\r\npersonId: " + personId;
            ret += "\r\nImage Count: " + imageCount;
            ret += "\r\nProfile Picture: " + profilePicture;

            ret += "\r\nImages:\r\n";
            foreach (ProfileImage img in images)
            {
                ret += "---\r\n";
                ret += img.ToString();
                ret += "\r\n---";
            }

            return ret;
        }
    }

    //struct for simplicity
    public struct ProfileImage : ProfileHandler.IScrollable
    {
        public Profile imageOwner;

        public int indexNumber; //includes deleted images
        public int number;      //excludes deleted images
        public string path;
        public string persistedFaceId;

        public string ImgPath { get { return path; } }

        public string DisplayName
        {
            get { return Constants.IMAGE_DISPLAY_LABEL + " " + (number + 1); }
        }

        public string IdentifyingName
        {
            get { return Path.Combine(imageOwner.folderName, Constants.IMAGE_LABEL + " " + indexNumber); }
        }

        public override string ToString()
        {
            string ret = "";
            ret += "Image Owner: " + imageOwner.displayName;
            ret += "\r\nIdentifyingName: " + IdentifyingName;
            ret += "\r\nDisplayName: " + DisplayName;
            ret += "\r\npersistedFaceId: " + persistedFaceId;
            ret += "\r\npath: " + path;
            ret += "\r\nindexNumber: " + indexNumber;
            ret += "\r\nnumber: " + number;
            return ret;
        }
    }

    public async Task MakeRequestAndSendInfoToROS<T>(FaceAPICall<T> call)
    {
        // TODO: make a library for face_msgs, which is used by the API library and by this unity game
        // then this struct conversion would be a lot cleaner...

        UnityFaceIDHelper.FaceAPIRequest libReq = call.request;
        FaceAPIRequest request = new FaceAPIRequest
        {
            request_method = (FaceAPIReqMethod)libReq.request_method,
            request_type = (FaceAPIReqType)libReq.request_type,
            content_type = (libReq.content_type == ContentType.CONTENT_STREAM) ? FaceAPIReqContentType.CONTENT_STREAM : FaceAPIReqContentType.CONTENT_JSON,
            request_parameters = libReq.request_parameters,
            request_body = libReq.request_body
        };
        rosManager.SendFaceAPIRequestAction(request);

        await call.MakeCallAsync();

        UnityFaceIDHelper.FaceAPIResponse libResp = call.response;
        FaceAPIResponse response = new FaceAPIResponse
        {
            response_type = (FaceAPIRespType) libResp.response_type,
            response = libResp.response
        };
        rosManager.SendFaceAPIResponseAction(response);
    }
                            
}
