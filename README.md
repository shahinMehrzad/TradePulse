# 📈 .NET Financial Market Analytics & Signal Platform

An enterprise-grade playground and learning platform designed to master advanced .NET concepts, software architecture, and high-performance programming through a real-world cryptocurrency/financial market monitoring system.

## 🚀 Key Architectural Highlights

- **Clean Architecture & DDD:** Strict separation of concerns with Domain, Application, Infrastructure, and Presentation layers, leveraging Entities, Value Objects, and Domain Events.
- **SOLID Principles & Design Patterns:** High maintainability, testability, and decoupled components using MediatR (CQRS pattern).
- **High Performance & Low-Allocation Memory:** Heavy usage of `Span<T>`, `Memory<T>`, `ref struct`, and optimized stack/heap management to minimize Garbage Collection (GC) pressure.
- **Asynchronous Messaging:** Event-driven architecture powered by **RabbitMQ** for decoupled data ingestion and real-time event publishing.
- **Caching Strategy:** Hybrid caching implementation using In-Memory Cache and **Redis** for high-frequency market data.
- **Background Workers:** Dedicated Worker Services for live data ingestion (OHLCV candles) and background processing jobs.
- **Real-time Communication:** Live notifications and data streaming using **SignalR**.
- **Multi-Tier Presentation:** Separated **Web API** layer and a **Razor MVC / Blazor** frontend client.
- **Containerization:** Fully Dockerized ecosystem for seamless deployment and local orchestration with Docker Compose.
