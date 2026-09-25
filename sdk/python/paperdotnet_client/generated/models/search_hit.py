from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class SearchHit(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The containerId property
    container_id: Optional[UUID] = None
    # The contentTypeId property
    content_type_id: Optional[UUID] = None
    # The createdBy property
    created_by: Optional[UUID] = None
    # The id property
    id: Optional[UUID] = None
    # The rank property
    rank: Optional[float] = None
    # The snippet property
    snippet: Optional[str] = None
    # The sourceType property
    source_type: Optional[str] = None
    # The title property
    title: Optional[str] = None
    # The updatedAt property
    updated_at: Optional[datetime.datetime] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SearchHit:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SearchHit
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SearchHit()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "containerId": lambda n : setattr(self, 'container_id', n.get_uuid_value()),
            "contentTypeId": lambda n : setattr(self, 'content_type_id', n.get_uuid_value()),
            "createdBy": lambda n : setattr(self, 'created_by', n.get_uuid_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "rank": lambda n : setattr(self, 'rank', n.get_float_value()),
            "snippet": lambda n : setattr(self, 'snippet', n.get_str_value()),
            "sourceType": lambda n : setattr(self, 'source_type', n.get_str_value()),
            "title": lambda n : setattr(self, 'title', n.get_str_value()),
            "updatedAt": lambda n : setattr(self, 'updated_at', n.get_datetime_value()),
            "workspaceId": lambda n : setattr(self, 'workspace_id', n.get_uuid_value()),
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
        writer.write_uuid_value("containerId", self.container_id)
        writer.write_uuid_value("contentTypeId", self.content_type_id)
        writer.write_uuid_value("createdBy", self.created_by)
        writer.write_uuid_value("id", self.id)
        writer.write_float_value("rank", self.rank)
        writer.write_str_value("snippet", self.snippet)
        writer.write_str_value("sourceType", self.source_type)
        writer.write_str_value("title", self.title)
        writer.write_datetime_value("updatedAt", self.updated_at)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

