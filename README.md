### HOW TO RUN THIS
YOU NEED TO CREATE THE LOCAL SERVERS YOURSELF, I HAVE THE PYTHON FILES/DOCKER COMPOSES I USE ON ANOTHER GIT REPO YOU CAN TAKE THEM AND USE THEM IF YOU WISH, IT IS EXTREMELY DIFFICULT TO STITCH TOGETHER EVERYTHING (SADLY)

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

LOOK INSIDE AuthApp.csproj

ARE THE ENDPOINTS CORRECT ? DO YOUR ENDPOINTS NEED AUTH HEADERS ? SET THEM THERE.

THEN:
```bash
dotnet run
```

IF IT SAYS NOW LISTENING ON: http://localhost:5042

YOU ARE GOOD (probably, we will actually see after you send the register request XD because the sql db might have broken or smtn)

NOW GO TO FRONTEND PROJECT OVER AT https://github.com/erenakyildiz-code/PublicBaratrumDesktopApp.git