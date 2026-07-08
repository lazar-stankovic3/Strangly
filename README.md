# Strangly

Strangly is a random real-time chat web application inspired by Omegle-style matching. Users can enter the app, get matched with another online user and communicate through real-time chat features.

The main goal of this project was to practice real-time web communication, authentication, user matching, MVC architecture and basic production/security concerns in ASP.NET Core.

## Live Demo

Demo: https://strangly.vercel.app

## Features

* Random user matching
* Real-time text chat with SignalR
* SignalR hubs for live communication
* ASP.NET Core MVC structure
* User authentication with cookies
* JWT authentication support
* Google login support
* Facebook login support
* SQL Server database with Entity Framework Core
* Database migrations and seeding
* Rate limiting for protection against abuse
* Swagger API documentation in development
* Static frontend assets served from `wwwroot`
* Stripe configuration support
* Production/development environment handling

## Tech Stack

### Backend

* ASP.NET Core / .NET 8
* C#
* ASP.NET Core MVC
* Entity Framework Core
* SQL Server
* SignalR
* Cookie Authentication
* JWT Bearer Authentication
* Google Authentication
* Facebook Authentication
* Rate Limiting
* Swagger
* Stripe configuration

### Frontend

* HTML
* CSS
* TypeScript / JavaScript
* SignalR browser client

### Tools

* Git / GitHub
* Visual Studio
* Docker
* LibMan

## Project Structure

```txt
Strangly/
├── OmegleCloneMVC/
│   ├── Controllers/        # MVC controllers
│   ├── Data/               # DbContext, migrations and seeding
│   ├── Hubs/               # SignalR hubs
│   ├── Migrations/         # EF Core migrations
│   ├── Models/             # Application models
│   ├── Services/           # Background/application services
│   ├── Views/              # MVC Razor views
│   ├── wwwroot/            # Static frontend files
│   ├── Program.cs          # App configuration
│   ├── appsettings.json
│   └── libman.json
├── Dockerfile
└── OmegleCloneMVC.sln
```

## Main Application Parts

### Authentication

The app supports cookie-based authentication for normal browser sessions and JWT authentication for API-style requests.

### SignalR Hubs

SignalR is used for real-time communication between matched users.

The project includes:

* `chatHub`
* `textChatHub`

### Rate Limiting

Rate limiting is configured to reduce spam, brute-force login attempts and excessive HTTP requests.

### Database

The app uses SQL Server with Entity Framework Core migrations and seed logic.

## How It Works

1. A user opens the application.
2. The app connects the user to the real-time communication layer.
3. The matching logic pairs the user with another available user.
4. Users exchange messages in real time through SignalR.
5. Authentication and database logic support user/session-related features.

## Getting Started

### Prerequisites

Make sure you have installed:

* .NET 8 SDK
* SQL Server
* Visual Studio or VS Code
* Docker, optional

## Setup

Clone the repository:

```bash
git clone https://github.com/lazar-stankovic3/Strangly.git
cd Strangly/OmegleCloneMVC
```

Configure the connection string in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "OmegleCloneMVCContext": "Server=localhost;Database=StranglyDb;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "Jwt": {
    "Key": "your-long-secret-key",
    "Issuer": "Strangly",
    "Audience": "StranglyUsers"
  },
  "Authentication": {
    "Google": {
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret"
    },
    "Facebook": {
      "AppId": "your-facebook-app-id",
      "AppSecret": "your-facebook-app-secret"
    }
  },
  "Stripe": {
    "SecretKey": "your-stripe-secret-key"
  }
}
```

Run the application:

```bash
dotnet restore
dotnet ef database update
dotnet run
```

In development mode, Swagger is enabled.

## Environment Variables

For production, sensitive values should be stored as environment variables, not directly in `appsettings.json`.

Recommended variables:

```txt
ConnectionStrings__OmegleCloneMVCContext
Jwt__Key
Jwt__Issuer
Jwt__Audience
Authentication__Google__ClientId
Authentication__Google__ClientSecret
Authentication__Facebook__AppId
Authentication__Facebook__AppSecret
Stripe__SecretKey
```

## Screenshots

Add screenshots here after capturing the app.

```md
![Landing page](screenshots/landing.png)
![Chat screen](screenshots/chat.png)
![Matching screen](screenshots/matching.png)
```

## What I Learned

While building this project, I practiced:

* ASP.NET Core MVC architecture
* Real-time communication with SignalR
* Random user matching logic
* Cookie and JWT authentication
* External login providers
* SQL Server with Entity Framework Core
* Rate limiting and basic abuse prevention
* Environment-based configuration
* Preparing an app for deployment

## Future Improvements

* Improve UI/UX design
* Add better moderation/reporting system
* Add typing indicators
* Add reconnect handling
* Add automated tests
* Improve deployment documentation
* Add better logging and monitoring
* Separate frontend and backend more clearly

## Author

Lazar Stanković
GitHub: github.com/lazar-stankovic3
