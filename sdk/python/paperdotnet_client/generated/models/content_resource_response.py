from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class ContentResourceResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The archivedAt property
    archived_at: Optional[datetime.datetime] = None
    # The archivedBy property
    archived_by: Optional[str] = None
    # The containerId property
    container_id: Optional[str] = None
    # The id property
    id: Optional[str] = None
    # The kind property
    kind: Optional[str] = None
    # The maxVersions property
    max_versions: Optional[int] = None
    # The name property
    name: Optional[str] = None
    # The parentId property
    parent_id: Optional[str] = None
    # The publishingEnabled property
    publishing_enabled: Optional[bool] = None
    # The version property
    version: Optional[int] = None
    # The versioning property
    versioning: Optional[str] = None
    # The workspaceId property
    workspace_id: Optional[str] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ContentResourceResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ContentResourceResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ContentResourceResponse()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "archivedAt": lambda n : setattr(self, 'archived_at', n.get_datetime_value()),
            "archivedBy": lambda n : setattr(self, 'archived_by', n.get_str_value()),
            "containerId": lambda n : setattr(self, 'container_id', n.get_str_value()),
            "id": lambda n : setattr(self, 'id', n.get_str_value()),
            "kind": lambda n : setattr(self, 'kind', n.get_str_value()),
            "maxVersions": lambda n : setattr(self, 'max_versions', n.get_int_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "parentId": lambda n : setattr(self, 'parent_id', n.get_str_value()),
            "publishingEnabled": lambda n : setattr(self, 'publishing_enabled', n.get_bool_value()),
            "version": lambda n : setattr(self, 'version', n.get_int_value()),
            "versioning": lambda n : setattr(self, 'versioning', n.get_str_value()),
            "workspaceId": lambda n : setattr(self, 'workspace_id', n.get_str_value()),
        }
        return fields

    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_datetime_value("archivedAt", self.archived_at)
        writer.write_str_value("archivedBy", self.archived_by)
        writer.write_str_value("containerId", self.container_id)
        writer.write_str_value("id", self.id)
        writer.write_str_value("kind", self.kind)
        writer.write_int_value("maxVersions", self.max_versions)
        writer.write_str_value("name", self.name)
        writer.write_str_value("parentId", self.parent_id)
        writer.write_bool_value("publishingEnabled", self.publishing_enabled)
        writer.write_int_value("version", self.version)
        writer.write_str_value("versioning", self.versioning)
        writer.write_str_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)


