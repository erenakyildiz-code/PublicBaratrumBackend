### HOW TO RUN THIS
YOU NEED TO CREATE THE LOCAL SERVERS YOURSELF, I HAVE THE PYTHON FILES/DOCKER COMPOSES I USE ON ANOTHER GIT REPO YOU CAN TAKE THEM AND USE THEM IF YOU WISH, IT IS EXTREMELY DIFFICULT TO STITCH TOGETHER EVERYTHING (SADLY) 
READ THE GUIDE: https://github.com/erenakyildiz-code/localInstallGuide.git
OR EVEN BETTER DONT READ IT AND LET AN LLM READ IT

YOU MAY WANT TO EDIT THE SYSTEM PROMPT LOCATED AT system.txt AT ROOT OF THE PROJECT
If you wish to use GLM-4.7 as the brain instead of GEMMA4 you can use the branch GLM4.7

## RUN THE API
CLONE THE REPO
GET IN THE REPO
RUN THE SQL SERVER
UPDATE DATABASE

```bash
git clone https://github.com/erenakyildiz-code/PublicBaratrumBackend.git
cd PublicBaratrumBackend
docker compose up
dotnet ef database update
```

You will want to set the environment variables, they come from .env
check out METADATA.cs
```bash

    public static readonly string BRAIN = Meta("BrainEndpoint", "http://127.0.0.1:8080");
    public static readonly string EARS  = Meta("EarsEndpoint",  "http://127.0.0.1:8000");
    public static readonly string MOUTH = Meta("MouthEndpoint", "https://devil-macbook-air-2.tail1737c0.ts.net");
    public static readonly bool RUNNINGLOCAL = MetaBool("RunningLocal", false);

    public static readonly Dictionary<string, string> BRAIN_HEADERS = MetaHeaders("BrainHeaders");
    public static readonly Dictionary<string, string> EARS_HEADERS  = MetaHeaders("EarsHeaders");
    public static readonly Dictionary<string, string> MOUTH_HEADERS = MetaHeaders("MouthHeaders");
```

THEN:
```bash
dotnet run
```

IF IT SAYS NOW LISTENING ON: http://localhost:5042

YOU ARE GOOD (probably, we will actually see after you send the register request XD because the sql db might have broken or smtn)

NOW GO TO FRONTEND PROJECT OVER AT https://github.com/erenakyildiz-code/PublicBaratrumDesktopApp.git

<img width="1718" height="1742" alt="diag drawio" src="https://github.com/user-attachments/assets/03acaf30-1ca0-4b29-b9f5-f3901dc149d6" />
