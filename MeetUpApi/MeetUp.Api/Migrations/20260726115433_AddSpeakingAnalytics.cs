using System;
using System.Collections.Generic;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetUp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeakingAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MeetingAnalytics",
                columns: table => new
                {
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    TotalDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    ParticipantCount = table.Column<int>(type: "integer", nullable: false),
                    SpeakingDistribution = table.Column<List<SpeakingShare>>(type: "jsonb", nullable: false),
                    WordCount = table.Column<int>(type: "integer", nullable: false),
                    AverageWordsPerMinute = table.Column<double>(type: "double precision", nullable: false),
                    ComputedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingAnalytics", x => x.MeetingId);
                    table.ForeignKey(
                        name: "FK_MeetingAnalytics_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ParticipantAudioActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    StartedSpeakingMs = table.Column<int>(type: "integer", nullable: false),
                    StoppedSpeakingMs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantAudioActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParticipantAudioActivities_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ParticipantAudioActivities_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioActivities_MeetingId_StartedSpeakingMs",
                table: "ParticipantAudioActivities",
                columns: new[] { "MeetingId", "StartedSpeakingMs" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantAudioActivities_UserId",
                table: "ParticipantAudioActivities",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeetingAnalytics");

            migrationBuilder.DropTable(
                name: "ParticipantAudioActivities");
        }
    }
}
